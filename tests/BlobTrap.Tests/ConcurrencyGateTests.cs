using System.Threading;
using System.Threading.Tasks;
using BlobTrap.Core.Download;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// O portão é a peça que faz a preferência "downloads simultâneos" significar alguma coisa.
/// Quando ele erra, nada quebra visivelmente: só passa mais gente do que o usuário permitiu.
/// </summary>
public class ConcurrencyGateTests
{
    [Fact]
    public async Task Acquire_AdmiteAteOLimiteEEnfileiraORestante()
    {
        var gate = new ConcurrencyGate(2);

        await gate.AcquireAsync();
        await gate.AcquireAsync();
        var third = gate.AcquireAsync();

        Assert.False(third.IsCompleted);
        Assert.Equal(2, gate.InUse);
        Assert.Equal(1, gate.Waiting);

        gate.Release();
        await third;

        Assert.Equal(2, gate.InUse);
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task AumentarOLimite_SoltaQuemJaEstavaNaFila()
    {
        var gate = new ConcurrencyGate(1);

        await gate.AcquireAsync();
        var queued = gate.AcquireAsync();
        Assert.False(queued.IsCompleted);

        gate.SetLimit(3);

        await queued;
        Assert.Equal(2, gate.InUse);
    }

    /// <summary>
    /// A regressão que motivou esta classe. Trocar a instância de SemaphoreSlim permitia
    /// limite_antigo + limite_novo em voo: baixar com 4 e reduzir para 1 dava 5.
    /// </summary>
    [Fact]
    public async Task ReduzirOLimite_NaoAdmiteNinguemNovoEnquantoOExcedenteNaoDrena()
    {
        var gate = new ConcurrencyGate(4);

        for (var i = 0; i < 4; i++) await gate.AcquireAsync();

        gate.SetLimit(1);
        var queued = gate.AcquireAsync();

        // Três liberações apenas drenam o excedente: 4 -> 3 -> 2 -> 1.
        gate.Release();
        gate.Release();
        gate.Release();

        Assert.False(queued.IsCompleted);
        Assert.Equal(1, gate.InUse);

        // A quarta é a que abre a única vaga que o novo limite permite.
        gate.Release();
        await queued;

        Assert.Equal(1, gate.InUse);
    }

    [Fact]
    public async Task ReduzirOLimite_NaoInterrompeQuemJaSeguraVaga()
    {
        var gate = new ConcurrencyGate(3);

        for (var i = 0; i < 3; i++) await gate.AcquireAsync();
        gate.SetLimit(1);

        // Ninguém foi expulso: o excedente continua contando até terminar por conta própria.
        Assert.Equal(3, gate.InUse);
        Assert.Equal(1, gate.Limit);
    }

    [Fact]
    public async Task SobCarga_NuncaPassaDoLimite()
    {
        var gate = new ConcurrencyGate(3);
        var current = 0;
        var peak = 0;
        var peakLock = new object();

        var workers = Enumerable.Range(0, 60).Select(async _ =>
        {
            await gate.AcquireAsync();
            try
            {
                var now = Interlocked.Increment(ref current);
                lock (peakLock) peak = Math.Max(peak, now);
                await Task.Delay(2);
            }
            finally
            {
                Interlocked.Decrement(ref current);
                gate.Release();
            }
        });

        await Task.WhenAll(workers);

        Assert.True(peak <= 3, $"pico de {peak} com limite 3");
        Assert.Equal(0, gate.InUse);
    }

    [Fact]
    public void Release_SemAcquire_Estoura()
    {
        var gate = new ConcurrencyGate(1);

        Assert.Throws<InvalidOperationException>(() => gate.Release());
    }

    [Fact]
    public void LimiteMenorQueUm_Rejeitado()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencyGate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencyGate(1).SetLimit(0));
    }
}
