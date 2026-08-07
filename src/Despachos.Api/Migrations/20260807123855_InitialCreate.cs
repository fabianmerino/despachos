using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Despachos.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "confirmacion_despacho",
                columns: table => new
                {
                    NroTransporte = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NroCompartimento = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Temperatura = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    APIDespachado = table.Column<decimal>(type: "decimal(8,4)", nullable: true),
                    VolObservado = table.Column<decimal>(type: "decimal(16,2)", nullable: true),
                    Vol60 = table.Column<decimal>(type: "decimal(16,2)", nullable: true),
                    FechaCompletado = table.Column<DateTime>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_confirmacion_despacho", x => new { x.NroTransporte, x.NroCompartimento });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "despachos_header",
                columns: table => new
                {
                    NroTransporte = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Terminal = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Mayorista = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PlacaVeh = table.Column<string>(type: "varchar(13)", maxLength: 13, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FechaCarga = table.Column<DateTime>(type: "date", nullable: false),
                    DNI = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Destino = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IndViaje = table.Column<string>(type: "varchar(1)", maxLength: 1, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BayQueuePriority = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Estado = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "Pendiente")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreadoEn = table.Column<DateTime>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_despachos_header", x => x.NroTransporte);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "outbox_confirmacion",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    NroTransporte = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Payload = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reintentos = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    MaxReintentos = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    Estado = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "Pendiente")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreadoEn = table.Column<DateTime>(type: "datetime", nullable: false),
                    UltimoIntentoEn = table.Column<DateTime>(type: "datetime", nullable: true),
                    ProximoIntentoEn = table.Column<DateTime>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_confirmacion", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "despachos_detail",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    NroTransporte = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NroEntrega = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CustomerCode = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Destinatario = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SCOP = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NroCompartimento = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Producto = table.Column<string>(type: "varchar(18)", maxLength: 18, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Volumen = table.Column<decimal>(type: "decimal(16,2)", nullable: false),
                    UMVol = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    API = table.Column<decimal>(type: "decimal(8,4)", nullable: true),
                    Estado = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "Pendiente")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_despachos_detail", x => x.Id);
                    table.ForeignKey(
                        name: "FK_despachos_detail_despachos_header_NroTransporte",
                        column: x => x.NroTransporte,
                        principalTable: "despachos_header",
                        principalColumn: "NroTransporte",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_despachos_detail_NroTransporte_NroCompartimento",
                table: "despachos_detail",
                columns: new[] { "NroTransporte", "NroCompartimento" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_despachos_header_Estado",
                table: "despachos_header",
                column: "Estado");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_confirmacion_Estado_CreadoEn",
                table: "outbox_confirmacion",
                columns: new[] { "Estado", "CreadoEn" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_confirmacion_NroTransporte",
                table: "outbox_confirmacion",
                column: "NroTransporte",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "confirmacion_despacho");

            migrationBuilder.DropTable(
                name: "despachos_detail");

            migrationBuilder.DropTable(
                name: "outbox_confirmacion");

            migrationBuilder.DropTable(
                name: "despachos_header");
        }
    }
}
