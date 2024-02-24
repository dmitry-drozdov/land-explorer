using Land.Control.Helpers;
using Land.Control.Models;
using Land.Control.Properties;
using Land.Core;
using Land.Core.Parsing.Tree;
using Land.Markup;
using Land.Markup.Binding;
using Land.Markup.Tree;
using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using System.Xml.Linq;
using SWF = System.Windows.Forms;

#pragma warning disable CA1031 // Do not catch general exception types

namespace Land.Control
{
	public partial class LandExplorerControl : UserControl, INotifyPropertyChanged
	{
		private void Command_MarkupTree_Delete_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				MarkupManager.RemoveElement((MarkupElement)MarkupTreeView.SelectedItem);
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_MarkupTree_RelinkSame_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				/// Переходим к точке, которую хотим перепривязать
				var point = (ConcernPoint)State.SelectedItem_MarkupTreeView.DataContext;

				if (EnsureLocationValid(point))
				{
					Editor.SetActiveDocumentAndOffset(
						point.Context.FileName,
						point.Location.Start
					);

					/// Выбираем сущности всех уровней, к которым можно привязаться в данном месте
					Command_Relink_Executed(State.SelectedItem_MarkupTreeView);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_MarkupTree_RelinkCurrent_Executed(object sender, RoutedEventArgs e)
		{
			Command_Relink_Executed(State.SelectedItem_MarkupTreeView);
		}

		private void Command_MissingTree_Delete_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				MarkupManager.RemoveElement(((RemapCandidates)MissingTreeView.SelectedItem).Point);
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_MarkupTree_DeleteWithSource_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				var points = GetLinearSequenceVisitor.GetPoints(
					new List<MarkupElement> { (MarkupElement)MarkupTreeView.SelectedItem }
				);
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_MarkupTree_TurnOn_Executed(object sender, RoutedEventArgs e)
		{

		}

		private void Command_MarkupTree_TurnOff_Executed(object sender, RoutedEventArgs e)
		{

		}

		private void Command_MissingTree_Relink_Executed(object sender, RoutedEventArgs e)
		{
			Command_Relink_Executed(State.SelectedItem_MissingTreeView);
		}

		private void Command_MissingTree_Accept_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				var parent = GetTreeViewItemParent(State.SelectedItem_MissingTreeView);

				if (parent != null)
				{
					MarkupManager.RelinkConcernPoint(
						(parent.DataContext as RemapCandidates).Point,
						State.SelectedItem_MissingTreeView.DataContext as RemapCandidateInfo
					);

					SetStatus("Точка перепривязана", ControlStatus.Success);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_Relink_Executed(TreeViewItem target)
		{
			try
			{
				var fileName = Editor.GetActiveDocumentName();
				var parsedFile = LogFunction(() => GetParsed(fileName), true, false);

				if (parsedFile != null)
				{
					State.PendingCommand = new PendingCommandInfo()
					{
						Target = target,
						Command = LandExplorerCommand.Relink,
						Document = parsedFile
					};

					ConcernPointCandidatesList.ItemsSource =
						GetConcernPointCandidates(
							parsedFile,
							Editor.GetActiveDocumentSelection(false),
							Editor.GetActiveDocumentSelection(true)
						);

					var point = target.DataContext is RemapCandidates pair
						? pair.Point
						: (ConcernPoint)target.DataContext;

					ConfigureMarkupElementTab(true, point);

					SetStatus("Перепривязка точки", ControlStatus.Pending);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_SelectPoint_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				var fileName = Editor.GetActiveDocumentName();
				var parsedFile = LogFunction(() => GetParsed(fileName), true, false);

				if (parsedFile != null)
				{
					State.PendingCommand = new PendingCommandInfo()
					{
						Target = State.SelectedItem_MarkupTreeView,
						Document = parsedFile,
						Command = LandExplorerCommand.SelectPoint
					};

					ConcernPointCandidatesList.ItemsSource =
						GetConcernPointCandidates(
							parsedFile,
							Editor.GetActiveDocumentSelection(false),
							Editor.GetActiveDocumentSelection(true)
						);

					ConfigureMarkupElementTab(true);

					SetStatus("Добавление точки", ControlStatus.Pending);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_AddPoint_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				var fileName = Editor.GetActiveDocumentName();
				var parsedFile = LogFunction(() => GetParsed(fileName), true, false);

				if (parsedFile != null)
				{
					var candidate = GetConcernPointCandidates(
						parsedFile,
						Editor.GetActiveDocumentSelection(false),
						Editor.GetActiveDocumentSelection(true)
					)
					.OfType<ExistingConcernPointCandidate>()
					.FirstOrDefault(c => c.Line == null
						&& c.Node.Type != Land.Core.Specification.Grammar.CUSTOM_BLOCK_RULE_NAME
					);

					if (candidate != null)
					{
						MarkupManager.AddConcernPoint(
							candidate.Node,
							null,
							parsedFile,
							candidate.ViewHeader,
							null,
							State.SelectedItem_MarkupTreeView?.DataContext as MarkupElement
						);

						if (State.SelectedItem_MarkupTreeView != null)
						{
							State.SelectedItem_MarkupTreeView.IsExpanded = true;
						}

						SetStatus("Привязка произведена", ControlStatus.Success);

						if (SettingsObject.EnableAutosave)
						{
							Command_Save_Executed(sender, e);
						}
					}
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_AddLand_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				var fileName = Editor.GetActiveDocumentName();
				var parsedFile = LogFunction(() => GetParsed(fileName), true, false);

				if (parsedFile != null)
				{
					MarkupManager.AddLand(parsedFile);

					if (SettingsObject.EnableAutosave)
					{
						Command_Save_Executed(sender, e);
					}
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_AddConcern_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				MarkupManager.AddConcern(
					"Новая функциональность",
					null,
					State.SelectedItem_MarkupTreeView?.DataContext as MarkupElement
				);

				if (State.SelectedItem_MarkupTreeView != null)
				{
					State.SelectedItem_MarkupTreeView.IsExpanded = true;
				}

				if (SettingsObject.EnableAutosave)
				{
					Command_Save_Executed(sender, e);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_Save_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (!String.IsNullOrWhiteSpace(MarkupFilePath))
				{
					MarkupManager.Serialize(MarkupFilePath, !SettingsObject.SaveAbsolutePath);

					SetStatus("Разметка сохранена", ControlStatus.Success);
				}
				else
				{
					Command_SaveAs_Executed(sender, e);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_SaveAs_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				var saveFileDialog = new SaveFileDialog()
				{
					AddExtension = true,
					DefaultExt = "landmark",
					Filter = "Файлы LANDMARK (*.landmark)|*.landmark|Все файлы (*.*)|*.*",
					InitialDirectory = Path.GetDirectoryName(MarkupFilePath),
					FileName = Path.GetFileName(MarkupFilePath)
				};

				if (saveFileDialog.ShowDialog() == true)
				{
					MarkupFilePath = saveFileDialog.FileName;
					MarkupManager.Serialize(MarkupFilePath, !SettingsObject.SaveAbsolutePath);

					SetStatus("Разметка сохранена", ControlStatus.Success);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_Open_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (HasUnsavedChanges)
				{
					switch (SWF.MessageBox.Show(
						"Сохранить изменения текущей разметки?",
						"Создание новой разметки",
						SWF.MessageBoxButtons.YesNoCancel,
						SWF.MessageBoxIcon.Question))
					{
						case SWF.DialogResult.Yes:
							Command_Save_Executed(sender, e);
							break;
						case SWF.DialogResult.Cancel:
							return;
					}
				}

				var openFileDialog = new OpenFileDialog()
				{
					AddExtension = true,
					DefaultExt = "landmark",
					Filter = "Файлы LANDMARK (*.landmark)|*.landmark|Все файлы (*.*)|*.*"
				};

				if (openFileDialog.ShowDialog() == true)
				{
					LoadFromFile(openFileDialog.FileName);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_New_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (HasUnsavedChanges)
				{
					switch (SWF.MessageBox.Show(
						"Сохранить изменения текущей разметки?",
						"Создание новой разметки",
						SWF.MessageBoxButtons.YesNoCancel,
						SWF.MessageBoxIcon.Question))
					{
						case SWF.DialogResult.Yes:
							Command_Save_Executed(sender, e);
							break;
						case SWF.DialogResult.Cancel:
							return;
					}
				}

				MarkupManager.Clear();
				MarkupFilePath = null;

				SetStatus("Вычисление разметки...", ControlStatus.Success);

				//SWF.MessageBox.Show("Вычисление разметки...");

				Fill();
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Fill()
		{
			var gqlFiles = Editor.GetAllFiles("graphql");

			var gqlFuncs = new Dictionary<string, List<ConcernPointCandidate>>();
			var gqlTypes = new Dictionary<string, List<ConcernPointCandidate>>();
			var groups = new Dictionary<string, Concern>(); // functionality name -> group (concern) in markup

			var d = new ResourceStats();

			foreach (var file in gqlFiles)
			{
				d.Start();
				var pFile = LogFunction(() => GetParsed(file), true, false);
				d.Stop(ref d.ParseGraphql);

				if (pFile == null)
				{
					// todo: handle
					continue;
				}


				d.Start();
				var funcsAndTypes = GetGraphqlFuncsAndTypes(pFile, gqlFuncs, gqlTypes);
				foreach (var c in funcsAndTypes.Funcs.OfType<ExistingConcernPointCandidate>())
				{
					var name = c.Node.Children.First().ToString();
					var group = MarkupManager.AddConcern(name);
					var groupName = name.ToLower().Replace("id: ", "");
					if (!groups.ContainsKey(groupName))
					{
						groups.Add(groupName, group);
					}

					MarkupManager.AddConcernPoint(
						c.Node,
						null,
						pFile,
						c.ViewHeader,
						"graphql schema",
						group,
						false
					);
				}
				d.Stop(ref d.AddGraphqlConcern);
			}


			SWF.MessageBox.Show("looking for go resolvers...");

			var goFiles = Editor.GetAllFiles("go");

			SWF.MessageBox.Show($"looking for go resolvers ({goFiles.Count()} files)...");

			var resolvers = new Dictionary<GoFuncNode, List<GoFuncNode>>();
			var resolversDoubt = new Dictionary<GoFuncNode, List<GoFuncNode>>(); // функции без аргументов и поля типа (надо оставить первых)
			var funcsPerReciever = new Dictionary<string, int>();
			var funcsPerPackage = new Dictionary<string, int>();

			var usedRecievers = new HashSet<string>(); // какие классы в итоге использовались

			ParsedFiles.Clear(); // debug
			foreach (var file in goFiles)
			{
				d.Start();
				var pFile = LogFunction(() => GetParsed(file, d), true, false);
				d.Stop(ref d.ParseGoTotal);

				d.Start();
				VisitGoResolvers(pFile, gqlFuncs, gqlTypes, resolvers, resolversDoubt, funcsPerReciever, funcsPerPackage);
				d.Stop(ref d.VisitGo);
			}

			SWF.MessageBox.Show($"matching ({resolvers.Count()} resolvers)...");

			int weigth(GoFuncNode node) { return funcsPerReciever[node.Reciever] + funcsPerPackage[node.Package]; }

			d.Start();
			foreach (var item in resolvers)
			{
				var max = item.Value[0];
				var maxN = weigth(max);
				// ищем функцию, которая принадлежит классу и пакету с наибольшим количество функций, подходящих нам
				foreach (var r in item.Value)
				{
					if (weigth(r) > maxN)
					{
						max = r;
						maxN = weigth(r);
					}
				}
				usedRecievers.Add(max.Reciever);
				var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(max.Node);
				c.NormalizedName = max.Name;

				MarkupManager.AddConcernPoint(
						(c as ExistingConcernPointCandidate).Node,
						null,
						max.ParsedFile,
						c.ViewHeader,
						null,
						groups[c.NormalizedName],
						false
				);
			}

			SWF.MessageBox.Show($"matching ({resolversDoubt.Count()} resolversDoubt)...");

			foreach (var item in resolversDoubt)
			{
				var cand = item.Value[0];
				if (!usedRecievers.Contains(cand.Reciever)) continue;

				// посчитать есть ли контекст и сколько строк в функции

				var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(cand.Node);
				c.NormalizedName = cand.Name;

				var name = cand.Node.Children[2].ToString();
				var group = MarkupManager.AddConcern(name);

				MarkupManager.AddConcernPoint(
						(c as ExistingConcernPointCandidate).Node,
						null,
						cand.ParsedFile,
						c.ViewHeader,
						null,
						group,
						false
				);

				var funcName = name.Replace("f_name: ", "").Replace("_", "").ToLower();
				var schemaLine = gqlTypes[funcName];
				var cSchema = schemaLine[0];

				/*MarkupManager.AddConcernPoint(
						(cSchema as ExistingConcernPointCandidate).Node,
						null,
						cand.ParsedFile,
						cSchema.ViewHeader,
						null,
						group,
						false
				);*/
			}

			d.Stop(ref d.AddGoConcern);

			SetStatus(d.ToString(), ControlStatus.Success);


			// debug
			var bad = 0;
			/*foreach (var group in groups)
			{
				if (group.Value.Elements.Count == 2)
				{
					group.Value.Name = "";
				}
				else bad++;
			}*/
			//SetStatus($"bad concerns: {bad}", ControlStatus.Success);
		}

		private void Command_Highlight_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				State.HighlightConcerns = !State.HighlightConcerns;

				if (!State.HighlightConcerns)
					Editor.ResetSegments();
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_OpenConcernGraph_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (MarkupManager.IsValid)
				{
					var graphWindow = new Window_ConcernGraph(MarkupManager);
					graphWindow.Show();
				}
				else
				{
					SetStatus(
						"Для работы с отношениями необходимо синхронизировать разметку с кодом",
						ControlStatus.Error
					);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_CollapseAll_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				CollapseOrExpand(MarkupTreeView, false);
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_AlwaysEnabled_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = true;
		}

		private void Command_HasUnsavedChanges_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = MarkupManager?.HasUnsavedChanges ?? false;
		}

		private void Command_MarkupTree_HasSelectedItem_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = MarkupTreeView?.SelectedItem != null;
		}

		private void Command_MarkupTree_HasSelectedConcernPoint_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = MarkupTreeView?.SelectedItem != null
				&& MarkupTreeView.SelectedItem is ConcernPoint;
		}

		private void Command_MissingTree_HasSelectedItem_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = MissingTreeView?.SelectedItem != null;
		}

		private void Command_MissingTree_HasSelectedConcernPoint_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = MissingTreeView?.SelectedItem != null
				&& MissingTreeView.SelectedItem is RemapCandidates;
		}

		private void Command_MissingTree_HasSelectedCandidate_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = MissingTreeView?.SelectedItem != null
				&& MissingTreeView.SelectedItem is RemapCandidateInfo;
		}

		private void Settings_Click(object sender, RoutedEventArgs e)
		{
			SettingsWindow = new Window_LandExplorerSettings(SettingsObject.Clone());
			SettingsWindow.Owner = Window.GetWindow(this);

			if (SettingsWindow.ShowDialog() ?? false)
			{
				SettingsObject = SettingsWindow.SettingsObject;

				LogAction(() => ReloadParsers(), true, true);

				if (File.Exists(Settings.Default.SettingsFilePath))
				{
					File.WriteAllText(
						Settings.Default.SettingsFilePath,
						SettingsSerializer.Serialize(SettingsObject)
					);
				}
				else
				{
					Settings.Default.SerializedSettings = SettingsSerializer.Serialize(SettingsObject);
					Settings.Default.Save();
				}
			}
		}

		private void ApplyMapping_Click(object sender, RoutedEventArgs e)
		{
			LogAction(() =>
			{
				var searchType = sender == ApplyMappingLocal
					? ContextFinder.SearchType.Local
					: ContextFinder.SearchType.Global;

				ProcessAmbiguities(
					MarkupManager.Remap(
						GetPointSearchArea(searchType)
							.Select(f => TryParse(f, null, out bool success, true))
							.Where(f => f != null)
							.ToList(),
						true,
						searchType
					),
					true
				);
			}, true, false);
		}

		#region Копирование-вставка

		private void Command_Copy_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (MarkupTreeView.IsKeyboardFocusWithin && State.SelectedItem_MarkupTreeView != null)
				{
					State.BufferedDataContext = (MarkupElement)State.SelectedItem_MarkupTreeView.DataContext;
					SetStatus("Элемент скопирован", ControlStatus.Pending);
				}
				else if (RelationSource.IsKeyboardFocusWithin && RelationSource.Tag != null)
				{
					State.BufferedDataContext = (MarkupElement)RelationSource.Tag;
					SetStatus("Элемент скопирован", ControlStatus.Pending);
				}
				else if (RelationTarget.IsKeyboardFocusWithin && RelationTarget.Tag != null)
				{
					State.BufferedDataContext = (MarkupElement)RelationTarget.Tag;
					SetStatus("Элемент скопирован", ControlStatus.Pending);
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		private void Command_Paste_Executed(object sender, RoutedEventArgs e)
		{
			try
			{
				if (State.BufferedDataContext != null)
				{
					if (RelationSource.IsKeyboardFocusWithin)
					{
						RelationSource.Tag = State.BufferedDataContext;
						RefreshRelationCandidates();
						State.BufferedDataContext = null;

						SetStatus("Элемент вставлен", ControlStatus.Ready);
					}
					else if (RelationTarget.IsKeyboardFocusWithin)
					{
						RelationTarget.Tag = State.BufferedDataContext;
						RefreshRelationCandidates();
						State.BufferedDataContext = null;

						SetStatus("Элемент вставлен", ControlStatus.Ready);
					}
				}
			}
			catch (Exception ex)
			{
				ShowExceptionWindow(ex);
			}
		}

		#endregion

		#region Methods

		private void LoadFromFile(string fileName)
		{
			MarkupManager.Deserialize(fileName, Parsers.Grammars);
			MarkupFilePath = fileName;

			var stubNode = new Node("");
			stubNode.SetLocation(new PointLocation(0, 0, 0), new PointLocation(0, 0, 0));

			MarkupManager.DoWithMarkup(elem =>
			{
				if (elem is ConcernPoint p)
				{
					p.NodeLocation = new SegmentLocation
					{
						Start = new PointLocation(0, 0, 0),
						End = new PointLocation(0, 0, 0)
					};

					if (p.LineContext != null)
					{
						p.LineLocation = new SegmentLocation
						{
							Start = new PointLocation(0, 0, 0),
							End = new PointLocation(0, 0, 0)
						};
					}

					p.HasIrrelevantLocation = true;
				}
			});

			MarkupTreeView.ItemsSource = MarkupManager.Markup;

			CollapseOrExpand(MarkupTreeView, true);

			SetStatus("Разметка загружена", ControlStatus.Success);
		}

		public GraphqlConcernPointCandidates GetGraphqlFuncsAndTypes(
			ParsedFile file,
			Dictionary<string, List<ConcernPointCandidate>> gqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> gqlTypes)
		{
			var nodes = MarkupManager.GetGraphqlFuncNodes(file.Root);
			var listFuncs = new List<ConcernPointCandidate>(nodes.Funcs.Count);
			foreach (var item in nodes.Funcs)
			{
				var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(item);
				listFuncs.Add(c);
				if (gqlFuncs.TryGetValue(c.NormalizedName, out var candidates))
					candidates.Add(c);
				else
					gqlFuncs.Add(c.NormalizedName, new List<ConcernPointCandidate>() { c });
			}

			var listTypes = new List<ConcernPointCandidate>(nodes.Types.Count);
			foreach (var item in nodes.Types)
			{
				var c = (ConcernPointCandidate)new ExistingConcernPointCandidate(item);
				listTypes.Add(c);
				if (gqlTypes.TryGetValue(c.NormalizedName, out var candidates))
					candidates.Add(c);
				else
					gqlTypes.Add(c.NormalizedName, new List<ConcernPointCandidate>() { c });
			}
			return new GraphqlConcernPointCandidates(listFuncs, listTypes);
		}


		/// <summary>
		///  Кандидаты (пока что) на резолверы. Reciever -> go func
		/// </summary>
		public void VisitGoResolvers(
			ParsedFile file,
			Dictionary<string, List<ConcernPointCandidate>> graphqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> graphqlTypes,
			Dictionary<GoFuncNode, List<GoFuncNode>> resFuncs,
			Dictionary<GoFuncNode, List<GoFuncNode>> resTypes,
			Dictionary<string, int> funcsPerReciever, // функции-тезки с разными резолверами
			Dictionary<string, int> funcsPerPackage
			)
		{
			var nodes = MarkupManager.GetGoNodes(file.Root);
			var types = GetGoTypes(nodes.Types);
			VisitGoResolverCandidates(file, nodes.Funcs, types, graphqlFuncs, graphqlTypes, resFuncs, resTypes, funcsPerReciever, funcsPerPackage);
		}
		/// <summary>
		/// Кандидаты на резолверы (у кого есть аргумент struct)
		/// </summary>
		public void VisitGoResolverCandidates(
			ParsedFile file,
			LinkedList<Node> nodes,
			Dictionary<string, GoTypeNode> goTypes,
			Dictionary<string, List<ConcernPointCandidate>> graphqlFuncs,
			Dictionary<string, List<ConcernPointCandidate>> graphqlTypes,
			Dictionary<GoFuncNode, List<GoFuncNode>> resFuncs,
			Dictionary<GoFuncNode, List<GoFuncNode>> resTypes,
			Dictionary<string, int> funcsPerReciever, // функции-тезки с разными резолверами
			Dictionary<string, int> funcsPerPackage // функции-тезки с разными резолверами
			)
		{
			var package = file.Root.Children[1].Children[0].ToString().Replace("ID: ", "");
			foreach (var node in nodes)
			{
				var candidate = (GoFuncNode)null;

				// func, f_reciever, f_name, f_args, f_returns

				var idx = 1;
				Node nextChild() { return node.Children[idx++]; }

				// f_reciever
				var child = nextChild();
				if (child.ToString() != "f_reciever" || child.Children.Count() == 0) continue;
				var reciver = child.Children.Last().Children.First().ToString().Replace("ID: ", "");


				// f_name
				child = nextChild();
				var name = child.ToString().Replace("f_name: ", "").Replace("_", "").ToLower();

				var (isFunc, isType) = (graphqlFuncs.ContainsKey(name), graphqlTypes.ContainsKey(name));
				if (!isFunc && !isType) continue;

				// f_args
				child = nextChild();
				var args = child.Children.Where(x => x.ToString().StartsWith("f_arg: "));

				var weight = 0;
				if (isType && args.Count() != 1) continue; // type having ctx arg is func

				switch (args.Count())
				{
					case 0:
						if (isFunc) continue;
						break;
					case 1:
					case 2:
						weight = 2;
						foreach (var arg in args) // always 1 or 2 args for resolver
						{
							var argType = arg.ToString().Replace("f_arg: ", "");
							if (argType == "anon_struct")
							{
								weight = 3;
								break;
							}
							if (goTypes.TryGetValue(argType, out var goType))
							{
								if (goType.GoType is StructType)
								{
									// this struct has been seen earlier
									weight = 3;
									break;
								}
							}
						}
						break;
					default:
						weight = 1; // странная библиотека, позволяющая аргументы записывать в линейном виде
						break;

				}


				// f_returns
				child = nextChild();
				if (child.Children.Count() == 0) continue;

				candidate = new GoFuncNode(file, node, reciver, package, name);

				if (isType && !isFunc)
				{
					if (resTypes.TryGetValue(candidate, out var types))
						types.Add(candidate);
					else
						resTypes.Add(candidate, new List<GoFuncNode>() { candidate });
					continue; // doesn't consider types in weights 
				}


				if (funcsPerReciever.TryGetValue(reciver, out _))
					funcsPerReciever[reciver] += weight;
				else
					funcsPerReciever.Add(reciver, weight);

				if (funcsPerPackage.TryGetValue(package, out _))
					funcsPerPackage[package] += weight;
				else
					funcsPerPackage.Add(package, weight);

				if (resFuncs.TryGetValue(candidate, out var funcs))
					funcs.Add(candidate);
				else
					resFuncs.Add(candidate, new List<GoFuncNode>() { candidate });
			}
		}

		/// <summary>
		/// Находит все Go объявленные типы в файле (пока только структуры)
		/// </summary>
		public Dictionary<string, GoTypeNode> GetGoTypes(LinkedList<Node> nodes)
		{
			var types = new Dictionary<string, GoTypeNode>();

			foreach (var typeDef in nodes)
			{
				var node = typeDef.Children.FirstOrDefault(x => x.ToString() == "struct_type");
				if (node == null) continue;

				var stName = node.Children.First().ToString().Replace("ID: ", "");
				var st = new StructType(stName);

				node = node.Children?.FirstOrDefault(x => x.ToString() == "anon_struct")?.
					Children?.FirstOrDefault(x => x.ToString() == "struct_content");
				if (node == null) continue;


				bool onlyOneField = node.Children.Count <= 2;
				string lastType = "";
				string lastDelim = "";
				node.Children.Reverse();
				foreach (var item in node.Children)
				{
					if (item.ToString() == "struct_delim")
					{
						lastDelim = item.Children[0].ToString().Contains(",") ? "," : "\n";
						continue;
					}


					var goTypes = item.Children.Where(x => x.ToString() == "go_type");
					if (goTypes.Count() > 1 || lastDelim == "\n" || onlyOneField) // embeded structs
					{
						lastType = goTypes.Last().Children.First(x => x.ToString() != "arr_ptr").ToString().Replace("ID: ", "");
					}

					if (types.TryGetValue(lastType, out var goType))
					{
						st.Fields.Add(goType.GoType);
					}
					else
					{
						st.Fields.Add(new SimpleType(lastType));
					}
				}

				if (types.ContainsKey(stName))
				{
					SWF.MessageBox.Show($"duplicated type! {stName}");
				}
				else
				{
					types.Add(stName, new GoTypeNode(node, st));
				}
			}

			return types;
		}

		private List<ConcernPointCandidate> GetConcernPointCandidates(
			ParsedFile file,
			SegmentLocation realSelection,
			SegmentLocation adjustedSelection)
		{
			/// Для выделения находим сущности, объемлющие его
			var candidates = MarkupManager.GetConcernPointCandidates(file.Root, realSelection)
				.Select(c => (ConcernPointCandidate)new ExistingConcernPointCandidate(c))
				.ToList();

			/// Проверяем, можно ли привязаться к строке
			if (adjustedSelection.Start.Line == adjustedSelection.End.Line)
			{
				var candidate = candidates
					.OfType<ExistingConcernPointCandidate>()
					.FirstOrDefault(c => c.Node.Location.Includes(adjustedSelection));

				if (candidate != null)
				{
					candidates.Insert(0, new StringConcernPointCandidate
					{
						Node = candidate.Node,
						Line = adjustedSelection,
						ViewHeader = "строка: "
							+ file.Text.Substring(adjustedSelection.Start.Offset, adjustedSelection.Length.Value).Trim()
					});
				}
			}

			/// Проверяем, можно ли обрамить его кастомным блоком
			if (CustomBlockValidator.IsValid(file.Root, adjustedSelection))
			{
				candidates.Add(new CustomConcernPointCandidate(
					realSelection, adjustedSelection, "Новый пользовательский блок"
				));
			}

			return candidates;
		}

		private void CollapseOrExpand(ItemsControl control, bool expand)
		{
			for (var i = 0; i < control.Items.Count; ++i)
			{
				var treeViewItem = (TreeViewItem)control.ItemContainerGenerator.ContainerFromIndex(i);

				if (treeViewItem != null)
				{
					treeViewItem.IsExpanded = expand;
					treeViewItem.UpdateLayout();

					CollapseOrExpand(treeViewItem, expand);
				}
			}
		}

		private void ShowExceptionWindow(Exception ex)
		{
			var exceptionWindow = new Window_Exception(ex.ToString());
			exceptionWindow.ShowDialog();
		}

		#endregion
	}
}