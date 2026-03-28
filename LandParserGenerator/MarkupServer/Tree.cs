using System.Collections.Generic;

namespace MarkupServer
{
	/// <summary>
	/// Внутренняя модель узла дерева разметки.
	/// Хранится в памяти и в persisted snapshot (.land/anchors.json).
	/// </summary>
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

		// внутренние поля (нужны для updateAnchor / метрик, но не отдаются в плагин)
		public bool IsManual { get; set; }
		public string Language { get; set; }
		public string AnchorKind { get; set; }
		public string AnchorFamily { get; set; }
		public string GqlTypeKind { get; set; }
		public string MethodNameNorm { get; set; }
		public string ParentNameNorm { get; set; }
		// raw-значение родителя (например имя GraphQL-типа или kind для type_def). Нужен для правильной группировки.
		public string ParentNameRaw { get; set; }
		public string ReturnTypeNorm { get; set; }
		public List<Arg> Args { get; set; }
		public int? OrdinalInParent { get; set; }
		public Dictionary<string, double> NeighborBag { get; set; }
	}

	/// <summary>
	/// DTO для протокола v2 (компактные имена полей).
	/// id/n/t/c/f/s/e соответствуют:
	/// - id: Id
	/// - n : Name
	/// - t : NodeType
	/// - c : Children
	/// - f : Filepath
	/// - s : StartOffset
	/// - e : EndOffset
	/// </summary>
	public class TreeNodeClientV2
	{
		public string id { get; set; }
		public string n { get; set; }
		/// <summary>"group" | "anchor"</summary>
		public string t { get; set; }

		public List<TreeNodeClientV2> c { get; set; }

		public string f { get; set; }
		public int? s { get; set; }
		public int? e { get; set; }
	}

	public class ListTreeResultV2
	{
		public List<TreeNodeClientV2> r { get; set; }
		public bool? fc { get; set; }
	}

	public class UpdateAnchorResultV2
	{
		public TreeNodeClientV2 u { get; set; }
		// parent group id + name (нужно, чтобы клиент мог переместить узел между группами без полного reload)
		public string pg { get; set; }
		public string pn { get; set; }
		// field group id + name (2-й уровень группировки: поле внутри GraphQL-типа)
		public string fg { get; set; }
		public string fn { get; set; }
	}

	public class ListTreeParams
	{
		public string folderPath { get; set; }
		/// <summary>Если true и на диске есть кэш — вернём его вместо полного сканирования.</summary>
		public bool preferCache { get; set; } = true;
		/// <summary>Если true — игнорируем кэш и делаем полный рескан (и перезаписываем кэш).</summary>
		public bool forceRescan { get; set; } = false;
	}

	public class UpdateAnchorParams { public string anchorId { get; set; } }

	public class AddAnchorParams
	{
		public string filePath { get; set; }
		public int offset { get; set; }
	}

	public class AddAnchorResultV2
	{
		public TreeNodeClientV2 a { get; set; }
		public bool? ex { get; set; }
		public string m { get; set; }
	}

}
