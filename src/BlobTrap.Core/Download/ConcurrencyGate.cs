namespace BlobTrap.Core.Download;

/// <summary>
/// Um portão de concorrência cujo limite muda enquanto há trabalho em voo.
///
/// <see cref="SemaphoreSlim"/> não serve para isto. Ele não expõe ajuste de limite, e a saída
/// óbvia — trocar a instância — está errada de um jeito silencioso: quem já esperava continua
/// preso na instância antiga, e quem chega usa a nova, então o total simultâneo passa a ser
/// <c>limite_antigo + limite_novo</c>. Baixar com 8 e reduzir para 1 daria 9, que é o oposto
/// exato do que o usuário pediu ao mexer na preferência.
///
/// Aqui o limite é um número, não uma instância. Baixá-lo não interrompe ninguém: quem já
/// segura uma vaga termina, e o excedente drena conforme cada um libera.
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private int _limit;
    private int _inUse;

    public ConcurrencyGate(int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "O limite tem que ser ao menos 1.");
        _limit = limit;
    }

    public int Limit { get { lock (_sync) return _limit; } }

    /// <summary>Vagas ocupadas agora. Pode passar de <see cref="Limit"/> logo após uma redução.</summary>
    public int InUse { get { lock (_sync) return _inUse; } }

    public int Waiting { get { lock (_sync) return _waiters.Count; } }

    /// <summary>
    /// Ajusta o limite. Aumentar solta imediatamente quem estiver na fila; diminuir não
    /// cancela nada, só deixa de repor as vagas que forem liberadas.
    /// </summary>
    public void SetLimit(int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "O limite tem que ser ao menos 1.");

        List<TaskCompletionSource>? admitted = null;

        lock (_sync)
        {
            if (limit == _limit) return;
            _limit = limit;

            while (_inUse < _limit && _waiters.Count > 0)
            {
                _inUse++;
                (admitted ??= new List<TaskCompletionSource>()).Add(_waiters.Dequeue());
            }
        }

        // Fora do lock: a continuação de quem espera não deve rodar segurando o cadeado.
        if (admitted is null) return;
        foreach (var waiter in admitted) waiter.TrySetResult();
    }

    public Task AcquireAsync()
    {
        TaskCompletionSource waiter;

        lock (_sync)
        {
            if (_inUse < _limit)
            {
                _inUse++;
                return Task.CompletedTask;
            }

            // RunContinuationsAsynchronously: sem isso, a continuação de quem esperava rodaria
            // dentro do Release da thread que acabou de terminar um download.
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        return waiter.Task;
    }

    public void Release()
    {
        TaskCompletionSource? next = null;

        lock (_sync)
        {
            if (_inUse == 0) throw new InvalidOperationException("Release sem Acquire correspondente.");

            // Acima do limite significa que ele foi reduzido: a vaga some em vez de ser repassada.
            if (_inUse <= _limit && _waiters.Count > 0) next = _waiters.Dequeue();
            else _inUse--;
        }

        next?.TrySetResult();
    }
}
