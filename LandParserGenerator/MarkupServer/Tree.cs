using System;
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

		// Только для shadow-узлов внутри user-групп: исходный AnchorId, на который ссылается тень.
		// Не сериализуется в snapshot — заполняется только в материализованных Roots.
		// На клиент уходит как поле `aid` в TreeNodeClientV2.
		public string RealAnchorId { get; set; }
		// Только для group-узлов: "auto" (по умолчанию для совместимости) или "user".
		// На клиент уходит как поле `gk` в TreeNodeClientV2.
		public string GroupKind { get; set; }
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
		public string k { get; set; }

		/// <summary>Group kind: "auto" (default, omitted) или "user" — отличает пользовательские группы от автогрупп.</summary>
		public string gk { get; set; }
		/// <summary>Real anchor id для shadow-узла (anchor внутри user-группы). Если null — узел канонический.</summary>
		public string aid { get; set; }
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


	public class DeleteAnchorParams
	{
		public string anchorId { get; set; }
	}

	public class DeleteAnchorResultV2
	{
		public bool? d { get; set; }
		public string m { get; set; }
	}

	public class ParentRebindOptionsParams
	{
		public string anchorId { get; set; }
	}

	public class ParentRebindTargetDescriptor
	{
		public string filePath { get; set; }
		public int? startOffset { get; set; }
		public int? endOffset { get; set; }
		public string anchorKind { get; set; }
	}

	public class ParentRebindApplyParams
	{
		public string anchorId { get; set; }
		public ParentRebindTargetDescriptor target { get; set; }
	}

	public class ParentRebindOptionV2
	{
		public TreeNodeClientV2 n { get; set; }
		public bool? c { get; set; }
	}

	public class ParentRebindOptionsResultV2
	{
		public TreeNodeClientV2 e { get; set; }
		public List<ParentRebindOptionV2> o { get; set; }
		public string m { get; set; }
	}

	// =========================================================================
	// User-группы (этап 1): персистентные пользовательские "папки", в которые
	// можно класть один и тот же якорь несколько раз.
	// =========================================================================

	/// <summary>
	/// Внутренняя модель пользовательской группы. Сохраняется в snapshot.
	/// </summary>
	public class UserGroup
	{
		public string Id { get; set; }                  // "userGroup:" + GUID
		public string Name { get; set; }
		public string ParentGroupId { get; set; }       // зарезервировано; сейчас всегда null
		public int? Order { get; set; }                 // сортировка соседей; null = по имени
		public DateTime CreatedAtUtc { get; set; }
		public DateTime UpdatedAtUtc { get; set; }
	}

	/// <summary>
	/// Запись о принадлежности якоря к user-группе. Один якорь может состоять
	/// в нескольких группах; в одной группе один якорь — один раз.
	/// </summary>
	public class UserGroupMembership
	{
		public string GroupId { get; set; }
		public string AnchorId { get; set; }
		public int? Order { get; set; }                 // сортировка внутри группы; null = по имени якоря
		public DateTime AddedAtUtc { get; set; }
	}

	// ----- Params/Result DTO для RPC user-групп -----

	public class CreateUserGroupParams
	{
		public string name { get; set; }
		public string parentGroupId { get; set; }       // зарезервировано; сейчас игнорируется
	}

	public class RenameUserGroupParams
	{
		public string groupId { get; set; }
		public string name { get; set; }
	}

	public class DeleteUserGroupParams
	{
		public string groupId { get; set; }
	}

	public class AddAnchorToGroupParams
	{
		public string anchorId { get; set; }
		public string groupId { get; set; }
	}

	public class RemoveAnchorFromGroupParams
	{
		public string anchorId { get; set; }
		public string groupId { get; set; }
	}

	public class UserGroupResultV2
	{
		public TreeNodeClientV2 g { get; set; }         // user-группа в форме TreeNodeClientV2 (gk="user")
		public string m { get; set; }
	}

	public class UserGroupSimpleResultV2
	{
		public bool? ok { get; set; }
		public string m { get; set; }
		public bool? alreadyMember { get; set; }        // только для add — уже состоит в группе
		public int? removedMemberships { get; set; }    // только для delete группы — сколько связок упало
	}

}
