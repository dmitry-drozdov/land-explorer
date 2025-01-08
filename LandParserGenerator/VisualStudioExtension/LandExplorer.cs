using Land.Control;
using Land.VisualStudioExtension.Listeners;

namespace Land.VisualStudioExtension
{
	using System;
	using System.Diagnostics;
	using System.Runtime.InteropServices;
	using System.Threading;
	using System.Windows;
	using Microsoft.VisualStudio;
	using Microsoft.VisualStudio.Shell;
	using Microsoft.VisualStudio.Shell.Interop;

	/// <summary>
	/// This class implements the tool window exposed by this package and hosts a user control.
	/// </summary>
	/// <remarks>
	/// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
	/// usually implemented by the package implementer.
	/// <para>
	/// This class derives from the ToolWindowPane class provided from the MPF in order to use its
	/// implementation of the IVsUIElementPane interface.
	/// </para>
	/// </remarks>
	[Guid("531dabfc-7ad8-4373-8ff7-b73b0d9d748d")]
	public class LandExplorer : ToolWindowPane, IVsWindowFrameNotify2
	{
		internal LandExplorerControl Control { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="LandExplorer"/> class.
		/// </summary>
		public LandExplorer() : base(null)
		{
			this.Caption = "LandExplorer";

			var control = new LandExplorerControl();
			var adapter = new EditorAdapter();

			var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);


			control.Initialize(adapter, $"{path}/Resources/Settings.xml");

			this.Content = Control = control;
		}

		protected override void OnCreate()
		{
			PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Critical;
			base.OnCreate();
		}

		protected override void Initialize()
		{
			PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Critical;
			base.Initialize();
		}


		public int OnClose(ref uint pgrfSaveOptions)
		{
			if (this.Control.HasUnsavedChanges)
			{
				switch (MessageBox.Show(
					"Сохранить изменения текущей разметки?",
					"Закрытие панели разметки",
					MessageBoxButton.YesNoCancel,
					MessageBoxImage.Question))
				{
					case MessageBoxResult.Yes:
						this.Control.Save();
						break;
					case MessageBoxResult.Cancel:
						return VSConstants.E_ABORT;
				}

			}

			return VSConstants.S_OK;
		}
	}
}
