using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Land.Core;
using Land.Markup;
using Land.Markup.Binding;
using Land.Markup.CoreExtension;
using System.ComponentModel;
using Land.Control.Models;
using System.Diagnostics;

namespace Land.Control
{
	public partial class LandExplorerControl : UserControl, INotifyPropertyChanged
	{
		public ParserManager Parsers { get; private set; } = new ParserManager();

		private void ReloadParsers()
		{
			Log.Clear();

			foreach (var ext in Parsers.Load(SettingsObject, CACHE_DIRECTORY, Log))
			{
				foreach (var file in ParsedFiles.Keys.Where(f => Path.GetExtension(f) == ext).ToList())
				{
					DocumentChangedHandler(file);
				}
			}
		}

		private ParsedFile GetParsed(string documentName)
		{
			return GetParsed(documentName, null);
		}

		private ParsedFile GetParsed(string documentName, ResourceStats d)
		{
			return TryParse(documentName, null, out bool success, false, d);
			/*return !String.IsNullOrEmpty(documentName)
				/// Если связанный с точкой файл разбирали и он не изменился с прошлого разбора,
				? ParsedFiles.ContainsKey(documentName) && ParsedFiles[documentName] != null
					/// возвращаем сохранённый ранее результат
					? ParsedFiles[documentName]
					/// иначе пытаемся переразобрать файл
					: ParsedFiles[documentName] = TryParse(documentName, null, out bool success, false, d)
				: null;*/
		}

		private ParsedFile ParseFragment(string parserCode, string sourceFileName, string text)
		{
			var parser = Parsers[parserCode];
			var node = parser.Parse(text, false).Item1;
			return new ParsedFile
			{
				Name = sourceFileName,
				Root = node,
				Text = text,
			};
		}

		/// <summary>
		/// Попытка распарсить заданный файл
		/// </summary>
		/// <param name="fileName">Имя файла</param>
		/// <param name="text">Текст, если его не нужно брать из самого файла с именем <paramref name="fileName"/></param>
		/// <param name="success">Признак успешности выполнения операции</param>
		/// <param name="dryRun">Признак того, что нужно выполнить все пре- и пост- операции, кроме самого парсинга</param>
		/// <returns></returns>
		private ParsedFile TryParse(string fileName, string text, out bool success, bool dryRun = false, ResourceStats d = null)
		{
			d?.Start();
			if (!String.IsNullOrEmpty(fileName))
			{
				var extension = Path.GetExtension(fileName);

				if (Parsers[extension] != null)
				{
					if (String.IsNullOrEmpty(text))
						text = GetText(fileName);

					d?.Stop(ref d.ParseGoLoadText);
					Core.Parsing.Tree.Node root = null;

					if (dryRun)
					{
						root = null;
						success = true;
					}
					else
					{
						var parser = Parsers[extension];
						d?.Start();
						var parseRes = parser.Parse(text, false);
						d?.Stop(ref d.ParseGoTotalLibOutside);
						root = parseRes.Item1;
						var d1 = parseRes.Item2;
						if (d != null)
						{
							d.ParseGoTotalLib += d1.Duration;
						}


						d?.Start();
						success = Parsers[extension].Log.All(l => l.Type != MessageType.Error);
						if (!success)
						{
							Debug($"FAILED {fileName}");
						}
						Parsers[extension].Log.ForEach(l => l.FileName = fileName);
						Log.AddRange(Parsers[extension].Log);
						d?.Stop(ref d.ParseGoLog);
					}

					return success ? new ParsedFile
					{
						Root = root,
						Text = text,
						Name = fileName
					} : null;
				}
				else
				{
					Log.Add(Message.Error($"Отсутствует парсер для файлов с расширением '{extension}'", null));
				}
			}

			success = false;
			return null;
		}
	}
}