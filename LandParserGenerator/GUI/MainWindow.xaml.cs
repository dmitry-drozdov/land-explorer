using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.Serialization.Json;
using Microsoft.Win32;
using ICSharpCode.AvalonEdit;
using Land.Core;
using Land.Core.Specification;
using Land.Core.Parsing;
using Land.Core.Parsing.Preprocessing;
using Land.Core.Parsing.Tree;
using Land.Markup;
using Land.Markup.CoreExtension;
using Land.Markup.Binding;
using Land.Control;

namespace Land.GUI
{
	/// <summary>
	/// Логика взаимодействия для MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		public const int COLUMN_CORRECTION_ITEM = 1;

		private readonly string APP_DATA_DIRECTORY = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\LanD IDE";
		private readonly string DOCUMENTS_DIRECTORY = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\LanD Workspace";
		private readonly string DOCUMENTS_DLL_DIRECTORY = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\LanD Workspace\dll";

		private Brush LightRed { get; set; } = new SolidColorBrush(Color.FromRgb(255, 200, 200));
		private SelectedTextColorizer Grammar_SelectedTextColorizer { get; set; }
		private SegmentsBackgroundRenderer File_SegmentColorizer { get; set; }

		private Land.Core.Parsing.BaseParser Parser { get; set; }
		private BasePreprocessor Preprocessor { get; set; }

		public MainWindow()
		{
			InitializeComponent();

			/// Обеспечиваем существование каталогов для сохранения данных приложения
			if (!Directory.Exists(APP_DATA_DIRECTORY))
				Directory.CreateDirectory(APP_DATA_DIRECTORY);

			if (!Directory.Exists(DOCUMENTS_DIRECTORY))
				Directory.CreateDirectory(DOCUMENTS_DIRECTORY);

			if (!Directory.Exists(DOCUMENTS_DLL_DIRECTORY))
				Directory.CreateDirectory(DOCUMENTS_DLL_DIRECTORY);

			/// Добавляем определения синтаксиса для дефолтного подсветчика
			ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.RegisterHighlighting("YACC", new[] { ".y", ".yacc" }, 
				ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(
					new System.Xml.XmlTextReader(new StreamReader($"../../Xshd/yacc.xshd", Encoding.Default)),
					ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance
				)
			);
			ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.RegisterHighlighting("Lex", new[] { ".lex" },
				ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(
					new System.Xml.XmlTextReader(new StreamReader($"../../Xshd/lex.xshd", Encoding.Default)),
					ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance
				)
			);

			/// Загружаем настройки панели разметки
			EditorAdapter = new EditorAdapter(this, LandExplorer);
		}

		private void Window_Closed(object sender, EventArgs e)
		{
		}

		private void MoveCaretToSource(SegmentLocation loc, ICSharpCode.AvalonEdit.TextEditor editor, bool selectText = true, int? tabToSelect = null)
		{
			if (loc != null)
			{
				var start = loc.Start.Offset;
				var end = loc.End.Offset;
				editor.ScrollToLine(editor.Document.GetLocation(start).Line);

				if (selectText)
					editor.Select(start, end - start + 1);
			}
			else
			{
				editor.Select(0, 0);
			}
		}

		private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
		{
			ScrollViewer scv = (ScrollViewer)sender;
			scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
			e.Handled = true;
		}

		private const int MIN_FONT_SIZE = 8;
		private const int MAX_FONT_SIZE = 40;

		private void Control_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
		{
			var controlSender = sender as System.Windows.Controls.Control;

			if (Keyboard.PrimaryDevice.Modifiers == ModifierKeys.Control)
			{
				e.Handled = true;
				double oldSize = controlSender.FontSize;
				if (e.Delta > 0 && controlSender.FontSize < MAX_FONT_SIZE)
					++controlSender.FontSize;
				else if(controlSender.FontSize > MIN_FONT_SIZE)
					--controlSender.FontSize;
			}
		}

		private void RecentItems_SetAsCurrentElement(ComboBox target, string filename, SelectionChangedEventHandler handler = null)
		{
			if (handler != null)
				target.SelectionChanged -= handler;

			if (target.Items.Contains(filename))
				target.Items.Remove(filename);

			target.Items.Insert(0, filename);
			target.SelectedIndex = 0;

			if (handler != null)
				target.SelectionChanged += handler;
		}

		private Encoding GetEncoding(string filename)
		{
			using (FileStream fs = File.OpenRead(filename))
			{
				Ude.CharsetDetector cdet = new Ude.CharsetDetector();
				cdet.Feed(fs);
				cdet.DataEnd();
				if (cdet.Charset != null)
				{
					return Encoding.GetEncoding(cdet.Charset);
				}
				else
				{
					return Encoding.Default;
				}
			}
		}

		#region Отладка перепривязки

		private Node NewTreeRoot { get; set; }
		private bool NewTextChanged { get; set; }

		private void ReplaceNewWithOldButton_Click(object sender, RoutedEventArgs e)
		{
			MappingDebug_NewTextEditor.Text = MappingDebug_OldTextEditor.Text;
		}

		private void MappingDebug_MarkupTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
		{
			if (MappingDebug_MarkupTreeView.SelectedItem is ConcernPoint point)
			{
				if (!point.HasInvalidLocation)
				{
					MappingDebug_OldTextEditor.Text = LandExplorer.GetText(point.Context.FileName);

					if (String.IsNullOrEmpty(MappingDebug_NewTextEditor.Text))
					{
						MappingDebug_NewTextEditor.Text = LandExplorer.GetText(point.Context.FileName);
					}

					MoveCaretToSource(point.Location, MappingDebug_OldTextEditor, true);
				}
			}
		}

		private void MainPerspectiveTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			MappingDebug_MarkupTreeView.ItemsSource = LandExplorer.GetMarkup();
		}

		private void MappingDebug_MarkupTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			if(MappingDebug_MarkupTreeView.SelectedItem is ConcernPoint)
			{
				MapPoint((ConcernPoint)MappingDebug_MarkupTreeView.SelectedItem);
			}
		}

		private void MapPoint(ConcernPoint point)
		{
			/// Если текст, к которому пытаемся перепривязаться, изменился
			if (NewTextChanged)
			{
				var parser = LandExplorer.Parsers[Path.GetExtension(point.Context.FileName)];

				/// и при этом парсер сгенерирован
				if (parser != null)
				{
					/// пытаемся распарсить текст
					NewTreeRoot = parser.Parse(MappingDebug_NewTextEditor.Text);
					var noErrors = parser.Log.All(l => l.Type != MessageType.Error);

					MappingDebug_StatusBar.Background = noErrors ? Brushes.LightGreen : LightRed;
					MappingDebug_StatusLabel.Content = noErrors ? String.Empty : "Ошибка при разборе нового текста";

					/// Если текст распарсился, ищем отображение из старого текста в новый
					if (noErrors)
					{
						NewTextChanged = false;
					}
				}
			}

			/// Если для текущего нового текста построено дерево и просчитано отображение
			if (!NewTextChanged)
			{
				var candidates = 
					LandExplorer.GetRebindingCandidates(point, MappingDebug_NewTextEditor.Text, NewTreeRoot)
					.ToList();

				MappingDebug_SimilaritiesList.ItemsSource = candidates;

				if (candidates.Any())
				{
					var weights = candidates.First().Weights;
					MappingDebug_SimilarityInfo.Text = weights != null
						? String.Join(Environment.NewLine, 
							new List<string>{
								$"wCore(H) = {weights[ContextType.HeaderCore]}",
								$"wNotCore(H) = {weights[ContextType.HeaderNonCore]}",
								$"wS = {weights[ContextType.Ancestors]}",
								$"wI = {weights[ContextType.Inner]}",
								$"wNearest(N) = {weights[ContextType.SiblingsNearest]}",
								$"wAll(N) = {weights[ContextType.SiblingsAll]}"
							}
						)
						: "Simple rebinding";
				}

				MoveCaretToSource(point.Location, MappingDebug_OldTextEditor);

				/// Если есть узлы в новом дереве, с которыми мы сравнивали выбранный узел старого дерева
				if (MappingDebug_SimilaritiesList.ItemsSource != null)
				{
					/// значит, в какой-то новый узел мы отобразили старый
					MappingDebug_SimilaritiesList.SelectedItem = candidates.FirstOrDefault();
					if(MappingDebug_SimilaritiesList.SelectedItem != null)
						MoveCaretToSource(((RemapCandidateInfo)MappingDebug_SimilaritiesList.SelectedItem).Node.Location, MappingDebug_NewTextEditor);
				}
			}
		}

		private void MappingDebug_SimilaritiesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if(MappingDebug_SimilaritiesList.SelectedItem != null)
			{
				var node = ((RemapCandidateInfo)MappingDebug_SimilaritiesList.SelectedItem).Node;
				MoveCaretToSource(node.Location, MappingDebug_NewTextEditor);
			}
		}

		private void NewTextEditor_TextChanged(object sender, EventArgs e)
		{
			NewTextChanged = true;
		}

		#endregion

		#region Тестирование панели разметки

		//public delegate void DocumentChangedHandler(string documentName);
		public Action<string> DocumentChangedCallback;

		public class DocumentTab
		{
			#region Editor

			private TextEditor _editor;

			private EditorSearchHandler QuickSearch { get; set; }

			public TextEditor Editor
			{
				get { return _editor; }

				set
				{
					_editor = value;

					if (_editor != null)
					{
						QuickSearch = new EditorSearchHandler(_editor.TextArea);
					}
					else
					{
						QuickSearch = null;
					}
				}
			}

			#endregion

			public string DocumentName { get; set; }

			public SegmentsBackgroundRenderer SegmentsColorizer { get; set; }	
		}

		public EditorAdapter EditorAdapter { get; set; }

		public Dictionary<TabItem, DocumentTab> Documents { get; set; } = new Dictionary<TabItem, DocumentTab>();

		private int NewDocumentCounter { get; set; } = 1;

		public DocumentTab CreateDocument(string documentName)
		{
			var tab = new TabItem();
			DocumentTabs.Items.Add(tab);

			Documents[tab] = new DocumentTab()
			{
				DocumentName = documentName,
				Editor = new TextEditor()
				{
					ShowLineNumbers = true,
					FontSize = 16,
					FontFamily = new FontFamily("Consolas")
				}
			};

			Documents[tab].Editor.TextArea.TextView.BackgroundRenderers
				.Add(Documents[tab].SegmentsColorizer = new SegmentsBackgroundRenderer(Documents[tab].Editor.TextArea));
			Documents[tab].Editor.TextChanged += Editor_TextChanged;

			tab.Content = Documents[tab].Editor;
			tab.Header = Path.GetFileName(Documents[tab].DocumentName);

			DocumentTabs.SelectedItem = tab;

			return Documents[tab];
		}

		private void Editor_TextChanged(object sender, EventArgs e)
		{
			var document = Documents.Values.FirstOrDefault(d => d.Editor == sender);

			if (document != null && !String.IsNullOrEmpty(document.DocumentName) 
				&& DocumentChangedCallback != null)
				DocumentChangedCallback(document.DocumentName);
		}

		public DocumentTab OpenDocument(string documentName)
		{
			if (File.Exists(documentName))
			{
				var stream = new StreamReader(documentName, Encoding.Default, true);
				var document = CreateDocument(documentName);

				document.Editor.Text = stream.ReadToEnd();
				document.Editor.SyntaxHighlighting = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager
					.Instance.GetDefinitionByExtension(Path.GetExtension(documentName));

				stream.Close();

				return document;
			}

			return null;
		}

		private void NewDocumentButton_Click(object sender, RoutedEventArgs e)
		{
			CreateDocument($"Новый документ {NewDocumentCounter++}");
		}

		private void SaveDocumentButton_Click(object sender, RoutedEventArgs e)
		{
			var activeTab = (TabItem)DocumentTabs.SelectedItem;

			if (activeTab != null)
			{
				if (!File.Exists(Documents[activeTab].DocumentName))
				{
					var saveFileDialog = new SaveFileDialog();
					if (saveFileDialog.ShowDialog() == true)
					{
						File.WriteAllText(saveFileDialog.FileName, Documents[activeTab].Editor.Text);
						Documents[activeTab].DocumentName = saveFileDialog.FileName;
						activeTab.Header = Path.GetFileName(saveFileDialog.FileName);
					}
				}
				else
				{
					File.WriteAllText(
						Documents[activeTab].DocumentName, 
						Documents[activeTab].Editor.Text
					);
				}
			}
		}

		private void CloseDocumentButton_Click(object sender, RoutedEventArgs e)
		{
			var activeTab = (TabItem)DocumentTabs.SelectedItem;

			if (activeTab != null)
			{
				/// Если файла для закрываемого таба не существует, и закрываемый текст непуст
				if ((String.IsNullOrEmpty(Documents[activeTab].DocumentName) 
					|| !File.Exists(Documents[activeTab].DocumentName)) 
					&& !String.IsNullOrEmpty(Documents[activeTab].Editor.Text)
					/// или если файл существует и его текст не совпадает с текстом в табе
					|| !String.IsNullOrEmpty(Documents[activeTab].DocumentName) 
					&& File.Exists(Documents[activeTab].DocumentName) 
					&& File.ReadAllText(Documents[activeTab].DocumentName) != Documents[activeTab].Editor.Text)
				{
					switch (MessageBox.Show(
						"В файле имеются несохранённые изменения. Сохранить текущую версию?",
						"Предупреждение",
						MessageBoxButton.YesNoCancel,
						MessageBoxImage.Question))
					{
						case MessageBoxResult.Yes:
							SaveDocumentButton_Click(null, null);
							break;
						case MessageBoxResult.No:
							break;
						case MessageBoxResult.Cancel:
							return;
					}
				}

				DocumentTabs.Items.Remove(activeTab);
				Documents.Remove(activeTab);
			}
		}

		private void OpenDocumentButton_Click(object sender, RoutedEventArgs e)
		{
			var openFileDialog = new OpenFileDialog();
			if (openFileDialog.ShowDialog() == true)
			{
				OpenDocument(openFileDialog.FileName);
			}
		}

		private void DocumentsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			var lb = sender as ListBox;

			if (lb.SelectedIndex != -1)
			{
				var msg = (Message)lb.SelectedItem;

				if (!String.IsNullOrEmpty(msg.FileName))
				{
					EditorAdapter.SetActiveDocumentAndOffset(msg.FileName, msg.Location);
				}
			}
		}

		#endregion
	}
}
