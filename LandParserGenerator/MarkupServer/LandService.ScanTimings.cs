using System.Diagnostics;

namespace MarkupServer
{
	/// <summary>
	/// Время последнего сканирования проекта (BuildSemanticMarkupFromDisk), по частям: разбор файлов парсером LanD,
	/// мешки соседей (AssignNeighborBags) и всё остальное — обход деревьев разбора и построение описаний якорей («извлечение»).
	/// Используется AnchorExtractCli --timing для замеров стоимости извлечения (markup/experiments, e11/e12 extract_cost).
	/// </summary>
	public partial class LandService
	{
		private readonly Stopwatch _swScan = new Stopwatch();
		private readonly Stopwatch _swParse = new Stopwatch();
		private readonly Stopwatch _swBags = new Stopwatch();

		internal sealed class ScanTimings
		{
			public double ParseMs;
			public double BagsMs;
			public double TotalMs;
			public double ExtractMs => TotalMs - ParseMs - BagsMs;
		}

		internal ScanTimings LastScanTimings { get; private set; }

		private void StartScanTimings()
		{
			_swScan.Reset();
			_swParse.Reset();
			_swBags.Reset();
			_swScan.Start();
		}

		private void FinishScanTimings()
		{
			_swScan.Stop();
			LastScanTimings = new ScanTimings
			{
				ParseMs = _swParse.Elapsed.TotalMilliseconds,
				BagsMs = _swBags.Elapsed.TotalMilliseconds,
				TotalMs = _swScan.Elapsed.TotalMilliseconds,
			};
		}
	}
}
