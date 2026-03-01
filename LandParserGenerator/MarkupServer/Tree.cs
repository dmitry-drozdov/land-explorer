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
		public string MethodNameNorm { get; set; }
		public string ParentNameNorm { get; set; }
		public string ReturnTypeNorm { get; set; }
		public List<Arg> Args { get; set; }
	}

	/// <summary>
	/// DTO, который уходит в плагин.
	///
	/// Оптимизация трафика:
	/// - для group-нод поля Filepath/StartOffset/EndOffset оставляем null,
	/// - для anchor-нод Children обычно null.
	///
	/// При включенном NullValueHandling.Ignore эти поля не попадут в JSON.
	/// </summary>
	public class TreeNodeClient
	{
		public string Id { get; set; }
		public string Name { get; set; }
		/// <summary>"group" | "anchor"</summary>
		public string NodeType { get; set; }

		public List<TreeNodeClient> Children { get; set; }

		public string Filepath { get; set; }
		public int? StartOffset { get; set; }
		public int? EndOffset { get; set; }
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
		public List<TreeNodeClient> Roots { get; set; }
		/// <summary>
		/// True если дерево вернули из кэша. Если false — поле может быть null,
		/// чтобы не засорять JSON (при включенном NullValueHandling.Ignore).
		/// </summary>
		public bool? FromCache { get; set; }
	}

	public class UpdateAnchorParams { public string anchorId { get; set; } }
	public class UpdateAnchorResult { public TreeNodeClient updatedNode { get; set; } }
}
