using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using TrendMicro.Tlsh;
using System.Net.Http.Headers;
using Land.Control;

namespace Land.Markup.Binding
{
	// TODO: нерабочий метод
	public static class FuzzyHashing
	{
		public const int MIN_TEXT_LENGTH = 25;
		public const int TLSH_HASH_LENGTH = 138;

		/// Хеш и расстояние автора (alexeyvale, 5aab975, 2021): нативный TLSH 3.9.1 из components/TLSH (окно 8, 256 корзин,
		/// контрольная сумма 3 байта, принудительный хеш от 50 байт) и расстояние totalDiff. Вызовы те же, что в коде автора.
		/// Сборка автора (C++/CLI, x64, .NET Framework) называется tlsh.dll, как и Tlsh.net, поэтому лежит в подпапке
		/// author_tlsh и загружается отражением. Включается флагом CfBench --fuzzy author. По умолчанию — замена 2025 года
		/// на Tlsh.net ниже: пустой хеш у текстов короче 512 байт и расстояние как число несовпавших байтов хеша
		/// (markup/experiments/e23_go_body_context).
		public static bool AuthorTlsh { get; set; }

		public static string AuthorTlshPath { get; set; }

		public static byte[] GetFuzzyHash(string text)
		{
			if (AuthorTlsh) return AuthorGetFuzzyHash(text);
			if (Encoding.UTF8.GetByteCount(text) < 512)
			{
				//throw new Exception($"{Encoding.UTF8.GetByteCount(text)}");
				return new byte[] { };
			}
			var t = new Tlsh();
			t.Update(Encoding.Unicode.GetBytes(text));
			var res = t.GetHash();
			return res.ToByteArray().ToArray();
		}

		public static double CompareTexts(string txt1, string txt2)
		{
			var hash1 = GetFuzzyHash(txt1);
			var hash2 = GetFuzzyHash(txt2);

			return CompareHashes(hash1, hash2);
		}

		public static double CompareHashes(byte[] hash1, byte[] hash2)
		{
			if (AuthorTlsh) return AuthorCompareHashes(hash1, hash2);
			var d = ComputeHashDistance(hash1, hash2);
			return Math.Max(0, (300 - d) / 300.0);
		}

		static Type AuthorHashObject;
		static MethodInfo AuthorFinal, AuthorGetHash, AuthorFromTlshStr, AuthorTotalDiff;
		static readonly object AuthorLock = new object();

		static void LoadAuthorTlsh()
		{
			if (AuthorHashObject != null) return;
			lock (AuthorLock)
			{
				if (AuthorHashObject != null) return;
				var path = AuthorTlshPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "author_tlsh", "tlsh.dll");
				var type = Assembly.LoadFrom(path).GetType("TLSH.HashObject", true);
				AuthorFinal = type.GetMethod("final", new[] { typeof(byte[]), typeof(uint), typeof(int) });
				AuthorGetHash = type.GetMethod("getHash", Type.EmptyTypes);
				AuthorFromTlshStr = type.GetMethod("fromTlshStr", new[] { typeof(byte[]) });
				AuthorTotalDiff = type.GetMethod("totalDiff", new[] { type });
				AuthorHashObject = type;
			}
		}

		/// using (var tlshObject = new TLSH.HashObject()) { tlshObject.final(textBytes, len, 1); return getHash().Take(138) }
		static byte[] AuthorGetFuzzyHash(string text)
		{
			LoadAuthorTlsh();
			using (var tlshObject = (IDisposable)Activator.CreateInstance(AuthorHashObject))
			{
				var textBytes = Encoding.Unicode.GetBytes(text);
				AuthorFinal.Invoke(tlshObject, new object[] { textBytes, (uint)textBytes.Length, 1 });

				return ((byte[])AuthorGetHash.Invoke(tlshObject, null))
					.Take(TLSH_HASH_LENGTH).ToArray();
			}
		}

		/// fromTlshStr для обоих хешей, затем Math.Max(0, (300 - tlsh1.totalDiff(tlsh2)) / 300.0)
		static double AuthorCompareHashes(byte[] hash1, byte[] hash2)
		{
			LoadAuthorTlsh();
			using (var tlsh1 = (IDisposable)Activator.CreateInstance(AuthorHashObject))
			using (var tlsh2 = (IDisposable)Activator.CreateInstance(AuthorHashObject))
			{
				AuthorFromTlshStr.Invoke(tlsh1, new object[] { hash1 });
				AuthorFromTlshStr.Invoke(tlsh2, new object[] { hash2 });

				return Math.Max(0, (300 - (int)AuthorTotalDiff.Invoke(tlsh1, new object[] { tlsh2 })) / 300.0);
			}
		}


		static int ComputeHashDistance(byte[] hash1, byte[] hash2)
		{
			if (hash1.Length != hash2.Length)
			{
				return Math.Max(hash1.Length, hash2.Length);
			}

			int distance = 0;
			for (int i = 0; i < hash1.Length; i++)
			{
				if (hash1[i] != hash2[i])
				{
					distance++;
				}
			}
			return distance;
		}


	}
}
