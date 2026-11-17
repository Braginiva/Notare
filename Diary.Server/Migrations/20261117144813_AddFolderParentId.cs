using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diary.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderParentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Folders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Folders");
        }
    }
}
