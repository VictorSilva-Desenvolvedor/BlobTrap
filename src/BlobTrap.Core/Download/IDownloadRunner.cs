using BlobTrap.Core.Models;

namespace BlobTrap.Core.Download;

/// <summary>
/// O que a fila precisa saber sobre quem executa um download: só "rode este job".
///
/// A costura existe para que <see cref="DownloadManager"/> — que decide ordem, limite de
/// concorrência e transição de estado — possa ser testado sem rede, sem ffmpeg e sem disco.
/// É justamente a parte cuja falha é silenciosa: um limite que não limita ou um estado que
/// regride não quebram nada visível, só entregam errado.
/// </summary>
public interface IDownloadRunner
{
    Task ExecuteAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellationToken);
}
