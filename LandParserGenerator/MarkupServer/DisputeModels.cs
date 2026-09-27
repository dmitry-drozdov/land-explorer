using System;
using System.Collections.Generic;

namespace MarkupServer
{
	// =========================================================================
	// Спорные компоненты перепривязки: точки прошлого снимка, которые претендуют на одних и тех же кандидатов
	// нового кода и которые шаг назначения (VPTreeLib.AssignmentRebind) не смог принять уверенно. Сохраняются
	// в снимке до решения пользователя; проценты вариантов и строк считаются на лету (VPTreeLib.AssignmentProbabilities).
	// =========================================================================

	/// <summary>Кандидат точки: живой якорь текущего снимка и расстояние до него.</summary>
	public class DisputeCandidate
	{
		public string AnchorId { get; set; }
		public double Dist { get; set; }
	}

	/// <summary>Точка спорной компоненты — снимок якоря прошлого состояния кода.</summary>
	public class DisputedPoint
	{
		public string AnchorId { get; set; }            // Id точки (прошлого якоря)
		public string Name { get; set; }
		public string Filepath { get; set; }
		public string Language { get; set; }
		public string AnchorKind { get; set; }
		public string AnchorFamily { get; set; }
		public string ParentNameRaw { get; set; }
		public string MethodNameNorm { get; set; }
		public string GqlTypeKind { get; set; }
		public string Comment { get; set; }             // комментарий на момент рескана
		public bool IsManual { get; set; }
		/// <summary>Есть данные пользователя: членство в папке, пользовательская связь, комментарий или ручная точка.</summary>
		public bool HasUserData { get; set; }
		/// <summary>Решение сервера при рескане: accepted (применено автоматически) | ambiguous | lost.</summary>
		public string Status { get; set; }
		/// <summary>
		/// Только для accepted: Id, который был у нового якоря до того, как сшивка передала ему Id точки.
		/// Нужен, чтобы отменить передачу, если пользователь выберет для точки другой вариант.
		/// </summary>
		public string AppliedFreshId { get; set; }
		public List<DisputeCandidate> Candidates { get; set; } = new();
	}

	public class DisputedComponent
	{
		public string Id { get; set; }                  // "dispute:" + хэш Id точек
		public DateTime CreatedAtUtc { get; set; }
		/// <summary>Цена варианта «не переносить» (R = τ / (1 − m) профиля на момент рескана).</summary>
		public double RejectCost { get; set; }
		public List<DisputedPoint> Points { get; set; } = new();
	}

	// ----- RPC: land/getDispute, land/resolveDispute -----

	/// <summary>Выбор пользователя для точки: target — Id живого якоря; null или "" — «не переносить».</summary>
	public class DisputeChoiceParam
	{
		public string point { get; set; }
		public string target { get; set; }
	}

	public class GetDisputeParams
	{
		/// <summary>Id компоненты; пусто — первая из ожидающих решения.</summary>
		public string disputeId { get; set; }
		/// <summary>Выборы пользователя, при которых считать проценты.</summary>
		public List<DisputeChoiceParam> fixedChoices { get; set; }
	}

	public class ResolveDisputeParams
	{
		public string disputeId { get; set; }
		/// <summary>Итоговая расстановка: по одному выбору на каждую точку компоненты.</summary>
		public List<DisputeChoiceParam> choices { get; set; }
	}

	/// <summary>Куда можно перенести точку: живой якорь (t — его Id) или «не переносить» (t = null).</summary>
	public class DisputeOptionV2
	{
		public string t { get; set; }
		public string n { get; set; }                   // имя якоря
		public string f { get; set; }                   // файл
		public int? s { get; set; }
		public int? e { get; set; }
		public string k { get; set; }
		public double? d { get; set; }                  // расстояние (для «не переносить» — цена R)
		/// <summary>
		/// Процент строки «точка → сюда» с учётом выбора пользователя в других строках; для строки, выбранной
		/// пользователем, — без её собственного выбора (видно, что предлагала модель).
		/// </summary>
		public double p { get; set; }
	}

	public class DisputePointV2
	{
		public string id { get; set; }                  // Id точки
		public string n { get; set; }
		public string f { get; set; }
		public string k { get; set; }
		public string cm { get; set; }
		public bool? man { get; set; }                  // ручная точка
		public bool? ud { get; set; }                   // есть данные пользователя
		public string st { get; set; }                  // accepted | ambiguous | lost
		public string auto { get; set; }                // что сервер применил автоматически (accepted) — Id якоря
		public string bt { get; set; }                  // лучший вариант без правок пользователя: куда идёт точка (null — никуда)
		public double bp { get; set; }                  // и процент этой строки без правок («было»)
		public string pin { get; set; }                 // выбор пользователя (эхо): Id, "" — «не переносить», null — выбора нет
		public List<DisputeOptionV2> o { get; set; } = new();
	}

	public class DisputeVariantV2
	{
		public double p { get; set; }
		public double c { get; set; }
		public List<string> a { get; set; } = new();    // по точкам: Id якоря или null
	}

	public class DisputeViewV2
	{
		public string id { get; set; }
		public string title { get; set; }
		public List<string> ids { get; set; } = new();  // все ожидающие решения компоненты (для навигации)
		public bool exact { get; set; }                 // проценты точные (перебор всех расстановок)
		public long variants { get; set; }
		public List<DisputePointV2> points { get; set; } = new();
		/// <summary>Лучшие расстановки при выборе пользователя (top[0] — показывается по умолчанию).</summary>
		public List<DisputeVariantV2> top { get; set; } = new();
		public string m { get; set; }
	}

	public class ResolveDisputeResultV2
	{
		public bool? ok { get; set; }
		public string m { get; set; }
		public int? moved { get; set; }                 // сколько точек перенесено
		public int? lost { get; set; }                  // сколько оставлено в _Lost
		public int? left { get; set; }                  // сколько спорных компонент ещё ждут решения
	}

	// ----- RPC: кандидаты одиночной точки (updateAnchor в спорном случае, recoverLostAnchor) -----

	public class RebindCandidateV2
	{
		public TreeNodeClientV2 n { get; set; }
		public double d { get; set; }
		public double p { get; set; }
	}

	public class LostCandidatesParams
	{
		public string lostAnchorId { get; set; }
	}

	public class RebindCandidatesResultV2
	{
		public List<RebindCandidateV2> cs { get; set; } = new();
		public double? pz { get; set; }                 // процент «не переносить»
		public string m { get; set; }
	}

	public class RebindAnchorToParams
	{
		public string anchorId { get; set; }
		public ParentRebindTargetDescriptor target { get; set; }
	}
}
