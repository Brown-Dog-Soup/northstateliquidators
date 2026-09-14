-- ============================================================================
-- Member welcome mail (docs/superpowers/specs/2026-09-13-member-welcome-email-design.md)
-- welcome_sent_at audit column + stamp proc; sp_RegisterMember result set widened.
-- Idempotent. Apply against sqldb-nsl-prod AFTER db/wishlist4.sql.
-- ============================================================================
SET NOCOUNT ON;

IF COL_LENGTH('dbo.members', 'welcome_sent_at') IS NULL
    ALTER TABLE dbo.members ADD welcome_sent_at DATETIME2 NULL;
GO

-- "Who never got their welcome?" should be a seek, not a scan.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_members_welcome_pending')
    CREATE INDEX IX_members_welcome_pending ON dbo.members (created_at DESC)
        WHERE welcome_sent_at IS NULL;
GO

IF OBJECT_ID('dbo.sp_StampMemberWelcomeSent', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_StampMemberWelcomeSent;
GO
CREATE PROCEDURE dbo.sp_StampMemberWelcomeSent
    @member_number CHAR(7)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.members SET welcome_sent_at = SYSUTCDATETIME()
    WHERE  member_number = @member_number;
END;
GO
GRANT EXECUTE ON dbo.sp_StampMemberWelcomeSent TO nsl_api;
GO

-- sp_RegisterMember: same logic as db/wishlist4.sql, result sets widened with
-- created_at + welcome_sent_at so the API needs no second round trip.
IF OBJECT_ID('dbo.sp_RegisterMember', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RegisterMember;
GO
CREATE PROCEDURE dbo.sp_RegisterMember
    @first_name NVARCHAR(100),
    @last_name  NVARCHAR(100),
    @email      NVARCHAR(320),
    @phone      VARCHAR(30)   = NULL,
    @city       NVARCHAR(120) = NULL,
    @state      VARCHAR(2)    = NULL,
    @zip        VARCHAR(10)   = NULL,
    @how_heard  NVARCHAR(200) = NULL,
    @source     NVARCHAR(40)  = 'web'
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    SET @email = LOWER(LTRIM(RTRIM(@email)));
    SET @state = UPPER(NULLIF(LTRIM(RTRIM(@state)), ''));

    BEGIN TRAN;
    DECLARE @lock INT;
    EXEC @lock = sp_getapplock @Resource = 'nsl_member_number', @LockMode = 'Exclusive',
                               @LockOwner = 'Transaction', @LockTimeout = 5000;
    IF @lock < 0
    BEGIN
        ROLLBACK TRAN;
        RAISERROR('Could not reserve a member number right now — please try again.', 16, 1);
        RETURN;
    END;

    DECLARE @existing CHAR(7) = (SELECT member_number FROM dbo.members WHERE email = @email);
    IF @existing IS NOT NULL
    BEGIN
        COMMIT TRAN;
        SELECT m.member_number, CAST(1 AS BIT) AS already_registered, m.created_at, m.welcome_sent_at
        FROM dbo.members m WHERE m.member_number = @existing;
        RETURN;
    END;

    DECLARE @yy CHAR(2) = RIGHT(CAST(YEAR(SYSDATETIMEOFFSET() AT TIME ZONE 'Eastern Standard Time') AS VARCHAR(4)), 2);
    DECLARE @next INT = ISNULL((SELECT MAX(CAST(RIGHT(member_number, 5) AS INT))
                                FROM dbo.members WHERE LEFT(member_number, 2) = @yy), 0) + 1;
    DECLARE @num CHAR(7) = @yy + RIGHT('00000' + CAST(@next AS VARCHAR(5)), 5);

    INSERT INTO dbo.members (member_number, first_name, last_name, email, phone, city, state, zip, how_heard, source)
    VALUES (@num, LTRIM(RTRIM(@first_name)), LTRIM(RTRIM(@last_name)), @email,
            NULLIF(LTRIM(RTRIM(@phone)), ''), NULLIF(LTRIM(RTRIM(@city)), ''), @state,
            NULLIF(LTRIM(RTRIM(@zip)), ''), NULLIF(LTRIM(RTRIM(@how_heard)), ''), COALESCE(@source, 'web'));
    COMMIT TRAN;

    SELECT @num AS member_number, CAST(0 AS BIT) AS already_registered,
           SYSUTCDATETIME() AS created_at, CAST(NULL AS DATETIME2) AS welcome_sent_at;
END;
GO
GRANT EXECUTE ON dbo.sp_RegisterMember TO nsl_api;
GO

PRINT 'member-welcome-mail: welcome_sent_at + sp_StampMemberWelcomeSent + widened sp_RegisterMember ready.';
