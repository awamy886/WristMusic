using System.Windows.Controls;

namespace WristMusic.Views;
public partial class LightPlayerPage : UserControl
{
    public LightPlayerPage() { InitializeComponent(); SeekSliderInput.Attach(Progress); }
}
