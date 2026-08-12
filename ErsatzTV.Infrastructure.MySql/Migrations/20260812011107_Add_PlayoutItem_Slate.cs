using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Add_PlayoutItem_Slate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SlateMediaItemId",
                table: "PlayoutItem",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayoutItem_SlateMediaItemId",
                table: "PlayoutItem",
                column: "SlateMediaItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlayoutItem_MediaItem_SlateMediaItemId",
                table: "PlayoutItem",
                column: "SlateMediaItemId",
                principalTable: "MediaItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlayoutItem_MediaItem_SlateMediaItemId",
                table: "PlayoutItem");

            migrationBuilder.DropIndex(
                name: "IX_PlayoutItem_SlateMediaItemId",
                table: "PlayoutItem");

            migrationBuilder.DropColumn(
                name: "SlateMediaItemId",
                table: "PlayoutItem");
        }
    }
}
