using Despachos.Api.Services;

namespace Despachos.Api.Tests;

public class WebhookCompletadoServiceTests
{
    [Fact]
    public async Task NroTransporteValido_EncolaEnNotifierYRetornaOk()
    {
        var notifier = new DespachoCompletadoNotifier();
        var sut = TestFactory.CreateWebhookCompletadoService(notifier);

        var result = await sut.ProcesarNotificacionAsync("0001234567", CancellationToken.None);

        Assert.False(result.IsLeft);
        Assert.Equal("0001234567", result.Right);

        var ok = notifier.Reader.TryRead(out var nro);
        Assert.True(ok);
        Assert.Equal("0001234567", nro);
    }

    [Fact]
    public async Task NroTransporteConEspacios_SeRecortaAntesDeEncolar()
    {
        var notifier = new DespachoCompletadoNotifier();
        var sut = TestFactory.CreateWebhookCompletadoService(notifier);

        await sut.ProcesarNotificacionAsync("  0001234567  ", CancellationToken.None);

        notifier.Reader.TryRead(out var nro);
        Assert.Equal("0001234567", nro);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NroTransporteVacio_RetornaValidationError(string? nroTransporte)
    {
        var notifier = new DespachoCompletadoNotifier();
        var sut = TestFactory.CreateWebhookCompletadoService(notifier);

        var result = await sut.ProcesarNotificacionAsync(nroTransporte, CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "NroTransporte");
        Assert.False(notifier.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NroTransporteExcedeLongitudMaxima_RetornaValidationError()
    {
        var notifier = new DespachoCompletadoNotifier();
        var sut = TestFactory.CreateWebhookCompletadoService(notifier);

        var result = await sut.ProcesarNotificacionAsync("12345678901", CancellationToken.None);

        Assert.True(result.IsLeft);
        Assert.Contains(result.Left!, e => e.Field == "NroTransporte");
        Assert.False(notifier.Reader.TryRead(out _));
    }
}
