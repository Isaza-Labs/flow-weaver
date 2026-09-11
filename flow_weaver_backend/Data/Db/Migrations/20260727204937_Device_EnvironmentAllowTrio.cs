using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Device_EnvironmentAllowTrio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before the trio there was a single IsQaLab flag and only env=qa
            // filtered targets — draft and production reached every device.
            // So the behaviour-preserving backfill is:
            //
            //   IsQaLab = true   ->  draft ✓  qa ✓  prod ✓
            //   IsQaLab = false  ->  draft ✓  qa ✗  prod ✓
            //
            // i.e. AllowQa carries the old value (plain rename) and the two
            // new columns backfill to TRUE for every existing row. Note the
            // EF scaffold defaults these to FALSE, which would park the whole
            // inventory — no run in any environment could target anything.
            migrationBuilder.RenameColumn(
                name: "IsQaLab",
                table: "devices",
                newName: "AllowQa");

            migrationBuilder.RenameColumn(
                name: "IsQaLab",
                table: "device_pools",
                newName: "AllowQa");

            // defaultValue also becomes the column DEFAULT, so rows inserted
            // outside EF (fixtures, manual SQL, an inventory sync that misses
            // the new fields) land on the same permissive legacy shape rather
            // than silently unreachable.
            migrationBuilder.AddColumn<bool>(
                name: "AllowDraft",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowProduction",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowDraft",
                table: "device_pools",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowProduction",
                table: "device_pools",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowDraft",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "AllowProduction",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "AllowDraft",
                table: "device_pools");

            migrationBuilder.DropColumn(
                name: "AllowProduction",
                table: "device_pools");

            migrationBuilder.RenameColumn(
                name: "AllowQa",
                table: "devices",
                newName: "IsQaLab");

            migrationBuilder.RenameColumn(
                name: "AllowQa",
                table: "device_pools",
                newName: "IsQaLab");
        }
    }
}
