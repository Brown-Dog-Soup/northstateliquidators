-- ============================================================================
-- Member street address (Rob, 2026-09-14): "Add lines for people to add their
-- physical address when they join. This will expedite whether or not we can
-- deliver within 20 miles and add onto their cost if they select it at
-- checkout." address1/address2 columns + sp_RegisterMember re-create.
-- Idempotent. Apply against sqldb-nsl-prod AFTER db/wishlist4.sql.
-- APPLY BEFORE DEPLOYING THE CODE — MembersFunction.ListSql and the CSV
-- export select address1/address2, so the staff members page 500s if the
-- code lands first.
-- ============================================================================
SET NOCOUNT ON;

IF COL_LENGTH('dbo.members', 'address1') IS NULL
    ALTER TABLE dbo.members ADD address1 NVARCHAR(200) NULL;
IF COL_LENGTH('dbo.members', 'address2') IS NULL
    ALTER TABLE dbo.members ADD address2 NVARCHAR(100) NULL;
GO

-- F3 sp_RegisterMember — idempotent on email (returns the existing number with
-- already_registered = 1). Member number = Eastern-local YY + 5-digit per-year
-- counter ('2600001'); an app lock serializes two signups in the same second.
-- Same body as db/wishlist4.sql, plus @address1/@address2 (both optional —
-- signup friction costs members, so the street address is not required).
IF OBJECT_ID('dbo.sp_RegisterMember', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_RegisterMember;
GO
CREATE PROCEDURE dbo.sp_RegisterMember
    @first_name NVARCHAR(100),
    @last_name  NVARCHAR(100),
    @email      NVARCHAR(320),
    @phone      VARCHAR(30)   = NULL,
    @address1   NVARCHAR(200) = NULL,
    @address2   NVARCHAR(100) = NULL,
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
    -- serialize number generation: two signups in the same second must not collide.
    -- sp_getapplock does NOT raise on timeout — it returns <0 and execution
    -- continues — so check the return code or a slow burst runs unserialized
    -- and the second INSERT dies on UQ_members_member_number instead.
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
        SELECT @existing AS member_number, CAST(1 AS BIT) AS already_registered;
        RETURN;
    END;

    -- year = Eastern local year ("first person in 2026 = 2600001"), 5-digit per-year counter
    DECLARE @yy CHAR(2) = RIGHT(CAST(YEAR(SYSDATETIMEOFFSET() AT TIME ZONE 'Eastern Standard Time') AS VARCHAR(4)), 2);
    DECLARE @next INT = ISNULL((SELECT MAX(CAST(RIGHT(member_number, 5) AS INT))
                                FROM dbo.members WHERE LEFT(member_number, 2) = @yy), 0) + 1;
    DECLARE @num CHAR(7) = @yy + RIGHT('00000' + CAST(@next AS VARCHAR(5)), 5);

    INSERT INTO dbo.members (member_number, first_name, last_name, email, phone, address1, address2, city, state, zip, how_heard, source)
    VALUES (@num, LTRIM(RTRIM(@first_name)), LTRIM(RTRIM(@last_name)), @email,
            NULLIF(LTRIM(RTRIM(@phone)), ''), NULLIF(LTRIM(RTRIM(@address1)), ''), NULLIF(LTRIM(RTRIM(@address2)), ''),
            NULLIF(LTRIM(RTRIM(@city)), ''), @state,
            NULLIF(LTRIM(RTRIM(@zip)), ''), NULLIF(LTRIM(RTRIM(@how_heard)), ''), COALESCE(@source, 'web'));
    COMMIT TRAN;

    SELECT @num AS member_number, CAST(0 AS BIT) AS already_registered;
END;
GO
-- Re-issue: DROP PROCEDURE above discards any grants.
GRANT EXECUTE ON dbo.sp_RegisterMember TO nsl_api;
GO

PRINT 'member-address: address1/address2 + sp_RegisterMember re-created (address1/address2 params, two-column result set unchanged).';
