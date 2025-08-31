using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	public static class VpTreeStorage
	{
		public static void SaveJson(string path, VpTreeSnapshot snap)
		{
			var json = JsonConvert.SerializeObject(snap, Formatting.Indented);
			File.WriteAllText(path, json);
		}

		public static VpTreeSnapshot LoadJson(string path)
		{
			var json = File.ReadAllText(path);
			return JsonConvert.DeserializeObject<VpTreeSnapshot>(json);
		}
	}
}
