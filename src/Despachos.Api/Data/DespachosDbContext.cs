using Microsoft.EntityFrameworkCore;
using Despachos.Api.Models;

namespace Despachos.Api.Data;

public sealed class DespachosDbContext : DbContext
{
    public DespachosDbContext(DbContextOptions<DespachosDbContext> options) : base(options) { }

    public DbSet<DespachoHeader> DespachosHeaders => Set<DespachoHeader>();
    public DbSet<DespachoDetail> DespachosDetails => Set<DespachoDetail>();
    public DbSet<ConfirmacionDespacho> ConfirmacionesDespacho => Set<ConfirmacionDespacho>();
    public DbSet<OutboxConfirmacion> OutboxConfirmaciones => Set<OutboxConfirmacion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DespachoHeader>(entity =>
        {
            entity.ToTable("despachos_header");
            entity.HasKey(e => e.NroTransporte);
            entity.Property(e => e.NroTransporte).HasMaxLength(10);
            entity.Property(e => e.Terminal).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Mayorista).HasMaxLength(20).IsRequired();
            entity.Property(e => e.PlacaVeh).HasMaxLength(13).IsRequired();
            entity.Property(e => e.FechaCarga).HasColumnType("date").IsRequired();
            entity.Property(e => e.DNI).HasMaxLength(8).IsRequired();
            entity.Property(e => e.Destino).HasMaxLength(20).IsRequired();
            entity.Property(e => e.IndViaje).HasMaxLength(1).IsRequired();
            entity.Property(e => e.BayQueuePriority).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Estado).HasMaxLength(20).IsRequired().HasDefaultValue(EstadoDespacho.Pendiente);
            entity.Property(e => e.CreadoEn).HasColumnType("datetime").IsRequired();

            entity.HasMany(e => e.Details)
                .WithOne(d => d.Header)
                .HasForeignKey(d => d.NroTransporte)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DespachoDetail>(entity =>
        {
            entity.ToTable("despachos_detail");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NroTransporte).HasMaxLength(10).IsRequired();
            entity.Property(e => e.NroEntrega).HasMaxLength(10).IsRequired();
            entity.Property(e => e.CustomerCode).HasMaxLength(16);
            entity.Property(e => e.Destinatario).HasMaxLength(16);
            entity.Property(e => e.SCOP).HasMaxLength(20);
            entity.Property(e => e.NroCompartimento).HasMaxLength(3).IsRequired();
            entity.Property(e => e.Producto).HasMaxLength(18).IsRequired();
            entity.Property(e => e.Volumen).HasColumnType("decimal(16,2)").IsRequired();
            entity.Property(e => e.UMVol).HasMaxLength(4).IsRequired();
            entity.Property(e => e.API).HasColumnType("decimal(8,4)");
            entity.Property(e => e.Estado).HasMaxLength(20).IsRequired().HasDefaultValue(EstadoDespacho.Pendiente);

            entity.HasIndex(e => new { e.NroTransporte, e.NroCompartimento }).IsUnique();
        });

        modelBuilder.Entity<ConfirmacionDespacho>(entity =>
        {
            entity.ToTable("confirmacion_despacho");
            entity.HasKey(e => new { e.NroTransporte, e.NroCompartimento });
            entity.Property(e => e.NroTransporte).HasMaxLength(10).IsRequired();
            entity.Property(e => e.NroCompartimento).HasMaxLength(3).IsRequired();
            entity.Property(e => e.Temperatura).HasColumnType("decimal(6,2)");
            entity.Property(e => e.APIDespachado).HasColumnType("decimal(8,4)");
            entity.Property(e => e.VolObservado).HasColumnType("decimal(16,2)");
            entity.Property(e => e.Vol60).HasColumnType("decimal(16,2)");
            entity.Property(e => e.FechaCompletado).HasColumnType("datetime").IsRequired();
        });

        modelBuilder.Entity<OutboxConfirmacion>(entity =>
        {
            entity.ToTable("outbox_confirmacion");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NroTransporte).HasMaxLength(10).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("longtext").IsRequired();
            entity.Property(e => e.Reintentos).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.MaxReintentos).IsRequired().HasDefaultValue(3);
            entity.Property(e => e.Estado).HasMaxLength(20).IsRequired().HasDefaultValue(OutboxEstado.Pendiente);
            entity.Property(e => e.CreadoEn).HasColumnType("datetime").IsRequired();
            entity.Property(e => e.UltimoIntentoEn).HasColumnType("datetime");
        });
    }
}
