-- ============================================================================
-- Member welcome mail (docs/superpowers/specs/2026-09-13-member-welcome-email-design.md)
-- welcome_sent_at audit column + stamp proc.
-- Idempotent. Apply against sqldb-nsl-prod AFTER db/wishlist4.sql.
-- APPLY BEFORE DEPLOYING the code — MembersFunction.ListSql and the CSV
-- export select welcome_sent_at, so the staff members page 500s if the code
-- lands first.
-- ============================================================================
SET NOCOUNT ON;

IF COL_LENGTH('dbo.members', 'welcome_sent_at') IS NULL
    ALTER TABLE dbo.members ADD welcome_sent_at DATETIME2 NULL;
GO

-- "Who never got their welcome?" should be a seek, not a scan.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_members_welcome_pending' AND object_id = OBJECT_ID('dbo.members'))
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

PRINT 'member-welcome-mail: welcome_sent_at + sp_StampMemberWelcomeSent ready.';
