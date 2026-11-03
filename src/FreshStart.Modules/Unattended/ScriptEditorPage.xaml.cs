using System.Windows;

namespace FreshStart.Modules.Unattended;

public partial class ScriptEditorPage
{
    public ScriptEditorPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ScriptEditorViewModel)?.LoadCommand.Execute(null);
    }
}
