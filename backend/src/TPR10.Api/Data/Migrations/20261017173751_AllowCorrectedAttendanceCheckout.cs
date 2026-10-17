using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowCorrectedAttendanceCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION attendance_preserve_event() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE original_in timestamptz; effective_in timestamptz;
                BEGIN
                    IF TG_OP='UPDATE' THEN
                        RAISE EXCEPTION 'Original attendance events are immutable' USING ERRCODE='23514';
                    END IF;
                    SELECT snapshot_at_utc,effective_in_utc INTO original_in,effective_in
                        FROM attendance_days WHERE id=NEW.day_id AND employee_id=NEW.actor_id;
                    -- Missing/wrong owners are rejected by the composite FK instead.
                    IF FOUND AND ((NEW.action='CheckIn' AND NEW.occurred_at_utc<>original_in)
                        OR (NEW.action='CheckOut' AND (NEW.occurred_at_utc<=effective_in
                            OR NOT (EXISTS(SELECT 1 FROM attendance_events WHERE day_id=NEW.day_id AND action='CheckIn')
                                OR EXISTS(SELECT 1 FROM attendance_adjustments a JOIN correction_requests r ON r.id=a.request_id
                                    WHERE a.day_id=NEW.day_id AND r.state='Approved' AND r.employee_id=NEW.actor_id
                                    AND a.after_in_utc=effective_in AND a.after_out_utc IS NULL
                                    AND a.after_version=(SELECT max(latest.after_version) FROM attendance_adjustments latest WHERE latest.day_id=NEW.day_id)))))) THEN
                        RAISE EXCEPTION 'Attendance checkout requires original or approved effective check-in' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE attendance_adjustments,attendance_days,attendance_events IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM attendance_adjustments) THEN
                        RAISE EXCEPTION 'Cannot downgrade checkout guard while retained adjustments exist';
                    END IF;
                END $$;
                CREATE OR REPLACE FUNCTION attendance_preserve_event() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE entered_at timestamptz;
                BEGIN
                    IF TG_OP='UPDATE' THEN
                        RAISE EXCEPTION 'Original attendance events are immutable' USING ERRCODE='23514';
                    END IF;
                    SELECT snapshot_at_utc INTO entered_at FROM attendance_days WHERE id=NEW.day_id AND employee_id=NEW.actor_id;
                    IF FOUND AND ((NEW.action='CheckIn' AND NEW.occurred_at_utc<>entered_at)
                        OR (NEW.action='CheckOut' AND (NEW.occurred_at_utc<=entered_at
                            OR NOT EXISTS(SELECT 1 FROM attendance_events WHERE day_id=NEW.day_id AND action='CheckIn')))) THEN
                        RAISE EXCEPTION 'Attendance event must follow original check-in' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }
    }
}
