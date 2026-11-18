using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sonic.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class VideoDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Downloads",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "Audio");

            migrationBuilder.AddColumn<string>(
                name: "Video_Codec",
                table: "Downloads",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Video_Container",
                table: "Downloads",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Video_EmbedChapters",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Video_EmbedMetadata",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Video_EmbedThumbnail",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Video_IncludeAutoSubtitles",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Video_MaxHeight",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Video_SubtitleFormat",
                table: "Downloads",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Video_SubtitleLanguages",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Video_SubtitleMode",
                table: "Downloads",
                type: "TEXT",
                maxLength: 12,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_Codec",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_Container",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_EmbedChapters",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_EmbedMetadata",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_EmbedThumbnail",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_IncludeAutoSubtitles",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_MaxHeight",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_SubtitleFormat",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_SubtitleLanguages",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Video_SubtitleMode",
                table: "Downloads");
        }
    }
}
