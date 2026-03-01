using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarkupServer
{
	public class TreeNode
	{
		// общие
		public string Id { get; set; }
		public string Name { get; set; }
		/// <summary>"group" | "anchor"</summary>
		public string NodeType { get; set; }

		// для групп
		public List<TreeNode> Children { get; set; } = new List<TreeNode>();

		// для якорей
		public string Filepath { get; set; }     // абсолютный путь
		public int? StartOffset { get; set; }
		public int? EndOffset { get; set; }
		public string MethodNameNorm { get; set; }
		public string ParentNameNorm { get; set; }
		public string ReturnTypeNorm { get; set; }
		public List<Arg> Args { get; set; }
	}

	public class ListTreeParams
	{
		public string folderPath { get; set; }
		/// <summary>Если true и на диске есть кэш — вернём его вместо полного сканирования.</summary>
		public bool preferCache { get; set; } = true;
		/// <summary>Если true — игнорируем кэш и делаем полный рескан (и перезаписываем кэш).</summary>
		public bool forceRescan { get; set; } = false;
	}
	public class ListTreeResult
	{
		public List<TreeNode> Roots { get; set; }
		public bool FromCache { get; set; }
	}

	public class UpdateAnchorParams { public string anchorId { get; set; } }
	public class UpdateAnchorResult { public TreeNode updatedNode { get; set; } }
}
