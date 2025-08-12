using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	public static class AnchorsIO
	{
		public static void SaveJson(string path, IEnumerable<MethodAnchor> anchors)
		{
			var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };
			File.WriteAllText(path, JsonConvert.SerializeObject(anchors, settings));
		}
		public static List<MethodAnchor> LoadJson(string path)
		{
			var txt = File.ReadAllText(path);
			var list = JsonConvert.DeserializeObject<List<MethodAnchor>>(txt);
			return list ?? new List<MethodAnchor>();
		}
	}
}
