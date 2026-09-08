using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VPTree;

namespace MarkupServer
{
	/// <summary>
	/// Кэш снимков VP-деревьев по профилям (.land/vp_index.json) — «вариант C» хранения
	/// (markup/experiments/e13_storage_size, §2б): якоря хранятся без мешков соседей, снимок дерева —
	/// необязательный кэш. Снимок действителен только для того же списка контекстов в том же порядке,
	/// с теми же мешками, весами и seed: всё это входит в Hash (LandService.TreeCacheHash). При несовпадении
	/// хэша дерево строится заново и кэш перезаписывается; любая ошибка чтения кэша означает обычное построение.
	/// Отключение: переменная окружения LAND_TREE_CACHE=0.
	/// </summary>
	internal sealed class PersistedProfileTree
	{
		public string Hash { get; set; }
		public int Count { get; set; }
		public string Weights { get; set; }
		public string BagKind { get; set; }
		public int Seed { get; set; } = 42;
		public DateTime BuiltAtUtc { get; set; }
		public VpTreeSnapshot Snapshot { get; set; }
	}

	internal sealed class PersistedTreeCache
	{
		public int Version { get; set; } = TreeCacheStore.CurrentVersion;
		public string FolderPath { get; set; }
		public DateTime UpdatedAtUtc { get; set; }
		public Dictionary<string, PersistedProfileTree> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}

	internal static class TreeCacheStore
	{
		public const int CurrentVersion = 1;
		private const string FolderName = ".land";
		private const string FileName = "vp_index.json";

		public static bool Enabled { get; set; } = !IsOff(Environment.GetEnvironmentVariable("LAND_TREE_CACHE"));

		private static bool IsOff(string s)
		{
			s = (s ?? "").Trim().ToLowerInvariant();
			return s == "0" || s == "off" || s == "false" || s == "no";
		}

		public static string GetPath(string folderPath)
			=> Path.Combine(Path.GetFullPath(folderPath), FolderName, FileName);

		/// <summary>Читает кэш; при любой проблеме (нет файла, другая версия, битый JSON) — null.</summary>
		public static PersistedTreeCache TryLoad(string folderPath)
		{
			try
			{
				var path = GetPath(folderPath);
				if (!File.Exists(path))
					return null;

				var cache = JsonConvert.DeserializeObject<PersistedTreeCache>(File.ReadAllText(path, Encoding.UTF8));
				if (cache == null || cache.Version != CurrentVersion)
					return null;

				cache.Profiles ??= new Dictionary<string, PersistedProfileTree>(StringComparer.OrdinalIgnoreCase);
				return cache;
			}
			catch
			{
				return null;
			}
		}

		/// <summary>Пишет кэш атомарно (tmp + move), компактным JSON — это машинный кэш, не документ.</summary>
		public static bool TrySave(string folderPath, PersistedTreeCache cache, out string error)
		{
			error = null;
			try
			{
				var path = GetPath(folderPath);
				Directory.CreateDirectory(Path.GetDirectoryName(path));

				cache.Version = CurrentVersion;
				cache.FolderPath = Path.GetFullPath(folderPath);
				cache.UpdatedAtUtc = DateTime.UtcNow;

				var json = JsonConvert.SerializeObject(cache, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
				var tmp = path + ".tmp";
				File.WriteAllText(tmp, json, Encoding.UTF8);
				if (File.Exists(path))
					File.Delete(path);
				File.Move(tmp, path);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}
	}
}
