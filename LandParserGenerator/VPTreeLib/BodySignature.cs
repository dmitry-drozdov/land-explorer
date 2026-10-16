using System.Text.RegularExpressions;

namespace VPTree
{
	/// <summary>
	/// Отпечаток тела функции — шестая компонента метрики (markup/experiments/e24_go_body_metric).
	///
	/// Тело без комментариев разбивается на токены (идентификатор, число или одиночный непробельный символ), из токенов
	/// берутся тройки подряд идущих (у тела короче трёх токенов — одна «тройка» из всех его токенов). Отпечаток — MinHash:
	/// для каждой из <see cref="Slots"/> хеш-функций минимум её значения по тройкам, хранятся старшие 16 бит (64 байта).
	/// Хранится в файле разметки у каждой точки, как нечёткий хеш внутреннего контекста у исходного алгоритма.
	///
	/// Расстояние — доля несовпавших значений: расстояние Хэмминга между векторами одной длины, делённое на постоянную,
	/// то есть метрика со значениями в [0, 1] (разные тела с одинаковым отпечатком дают 0 — неравенству треугольника это
	/// не мешает). Доля совпавших значений приближает коэффициент Жаккара множеств троек. Пустое тело (или его нет) —
	/// null: два пустых — 0, пустое и непустое — 1; это тоже сохраняет неравенство треугольника.
	/// </summary>
	public static class BodySignature
	{
		public const int Slots = 32;
		public const int Bytes = Slots * 2;

		static readonly Regex Comments = new Regex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
		static readonly Regex Tokens = new Regex(@"[A-Za-z_]\w*|\d+|\S", RegexOptions.Compiled);
		static readonly ulong[] Seeds = MakeSeeds();

		static ulong[] MakeSeeds()
		{
			var s = new ulong[Slots];
			ulong x = 42;
			for (int i = 0; i < Slots; i++)
				s[i] = Mix(x += 0x9E3779B97F4A7C15UL);
			return s;
		}

		/// <summary>Финализатор SplitMix64.</summary>
		static ulong Mix(ulong z)
		{
			z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
			z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
			return z ^ (z >> 31);
		}

		/// <summary>FNV-1a 64 по токенам тройки с разделителем 0x1F.</summary>
		static ulong ShingleHash(List<string> tokens, int from, int count)
		{
			ulong h = 14695981039346656037UL;
			for (int t = from; t < from + count; t++)
			{
				if (t > from) { h ^= 0x1F; h *= 1099511628211UL; }
				foreach (var c in tokens[t])
				{
					h ^= c;
					h *= 1099511628211UL;
				}
			}
			return h;
		}

		/// <summary>Отпечаток текста тела (между фигурными скобками); null, если токенов нет.</summary>
		public static byte[]? Compute(string? body)
		{
			if (string.IsNullOrEmpty(body)) return null;
			var tokens = Tokens.Matches(Comments.Replace(body, " ")).Select(m => m.Value).ToList();
			if (tokens.Count == 0) return null;
			var mins = new ulong[Slots];
			Array.Fill(mins, ulong.MaxValue);
			int n = tokens.Count < 3 ? 1 : tokens.Count - 2, len = tokens.Count < 3 ? tokens.Count : 3;
			for (int i = 0; i < n; i++)
			{
				var h = ShingleHash(tokens, i, len);
				for (int s = 0; s < Slots; s++)
				{
					var v = Mix(h ^ Seeds[s]);
					if (v < mins[s]) mins[s] = v;
				}
			}
			var sig = new byte[Bytes];
			for (int s = 0; s < Slots; s++)
			{
				var top = (ushort)(mins[s] >> 48);
				sig[2 * s] = (byte)top;
				sig[2 * s + 1] = (byte)(top >> 8);
			}
			return sig;
		}

		/// <summary>Доля несовпавших значений отпечатков; два пустых — 0, пустой и непустой — 1.</summary>
		public static double Distance(byte[]? a, byte[]? b)
		{
			bool ea = a == null || a.Length == 0, eb = b == null || b.Length == 0;
			if (ea && eb) return 0.0;
			if (ea || eb) return 1.0;
			if (ReferenceEquals(a, b)) return 0.0;
			int slots = Math.Min(a!.Length, b!.Length) / 2, diff = 0;
			for (int s = 0; s < slots; s++)
				if (a[2 * s] != b[2 * s] || a[2 * s + 1] != b[2 * s + 1]) diff++;
			return slots == 0 ? 1.0 : (double)diff / slots;
		}
	}
}
