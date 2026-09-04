using Xunit;

// Parte da suite mexe em estado global de processo: Log.Directory e Log.MinimumLevel sao
// estaticos, porque um logger e' um logger. Com classes rodando em paralelo, um teste do
// DownloadManager escrevia no log enquanto LogTests apagava a pasta - e o resultado era uma
// falha que depende de quem chegou primeiro.
//
// A suite inteira roda em menos de meio segundo, entao o paralelismo nao estava comprando
// nada em troca desse risco.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
