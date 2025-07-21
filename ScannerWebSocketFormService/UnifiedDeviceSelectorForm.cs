using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService;

public partial class UnifiedDeviceSelectorForm : Form
{
    public ScannerDevice? SelectedDevice { get; private set; }
    
    private readonly List<ScannerDevice> _initialDevices;
    private List<ScannerDevice> _currentDevices;
    private List<ScannerDevice> _sortedDevices;
    
    private readonly Func<Task<List<ScannerDevice>>>? _refreshDevicesCallback;
    
    private ListBox _listBoxDevices;
    private Button _buttonSelect;
    private Button _buttonCancel;
    private Button _buttonRefresh;
    private Label _labelTitle;
    private Label _labelCount;
    private Label _labelStatus;
    private ProgressBar _progressBar;
    private Panel _buttonPanel;

    public UnifiedDeviceSelectorForm(List<ScannerDevice> devices, Func<Task<List<ScannerDevice>>>? refreshCallback)
    {
        _initialDevices = devices ?? new List<ScannerDevice>();
        _currentDevices = new List<ScannerDevice>(_initialDevices);
        _sortedDevices = new List<ScannerDevice>();
        _refreshDevicesCallback = refreshCallback;
        
        InitializeComponent();
        ConfigureForm();
        LoadDevicesOptimized();
    }

    private void ListBoxDevices_DrawItem(object sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        
        if (e.Index >= 0 && e.Index < _sortedDevices.Count)
        {
            var device = _sortedDevices[e.Index];
            var isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            
            // Colores
            var backColor = isSelected ? Color.FromArgb(0, 120, 215) : Color.White;
            var textColor = isSelected ? Color.White : Color.FromArgb(64, 64, 64);
            var iconColor = isSelected ? Color.White : GetDeviceTypeColor(device.Type);
            
            // Fondo
            using (var brush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            
            // Icono del tipo de dispositivo
            var icon = GetTypeIcon(device.Type);
            var iconBounds = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 3, 20, 18);
            using (var iconBrush = new SolidBrush(iconColor))
            using (var iconFont = new Font("Segoe UI", 10F))
            {
                e.Graphics.DrawString(icon.Split(' ')[0], iconFont, iconBrush, iconBounds);
            }
            
            // Nombre del dispositivo
            var textBounds = new Rectangle(e.Bounds.X + 38, e.Bounds.Y + 3, e.Bounds.Width - 45, 18);
            using (var textBrush = new SolidBrush(textColor))
            using (var textFont = new Font("Segoe UI", 9F, FontStyle.Regular))
            {
                var displayText = $"{device.Type} - {device.Name}";
                e.Graphics.DrawString(displayText, textFont, textBrush, textBounds);
            }
        }
        else if (e.Index >= 0)
        {
            // Mensaje cuando no hay dispositivos o está cargando
            var message = _listBoxDevices.Items[e.Index].ToString();
            using (var brush = new SolidBrush(Color.FromArgb(120, 120, 120)))
            using (var font = new Font("Segoe UI", 9F, FontStyle.Italic))
            {
                var textBounds = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 3, e.Bounds.Width - 20, 18);
                e.Graphics.DrawString(message, font, brush, textBounds);
            }
        }
        
        e.DrawFocusRectangle();
    }
    
    private Color GetDeviceTypeColor(ScannerType type)
    {
        return type switch
        {
            ScannerType.TWAIN => Color.FromArgb(138, 43, 226), 
            ScannerType.WIA => Color.FromArgb(30, 144, 255),   
            _ => Color.FromArgb(100, 100, 100)
        };
    }
    
    private void ListBoxDevices_SelectedIndexChanged(object sender, EventArgs e)
    {
        var hasSelection = _listBoxDevices.SelectedIndex >= 0 && 
                          _listBoxDevices.SelectedIndex < _sortedDevices.Count;
        
        if (hasSelection)
        {
            var selectedDevice = _sortedDevices[_listBoxDevices.SelectedIndex];
            
            _labelStatus.Text = $"📋 Seleccionado: {selectedDevice.Type} - {selectedDevice.Name}";
            _labelStatus.ForeColor = Color.FromArgb(0, 120, 215);
            
            _buttonSelect.Enabled = true;
            _buttonSelect.BackColor = Color.FromArgb(40, 167, 69);
        }
        else
        {
            _labelStatus.Text = "Selecciona un dispositivo de la lista";
            _labelStatus.ForeColor = Color.FromArgb(100, 100, 100);
            _buttonSelect.Enabled = false;
            _buttonSelect.BackColor = Color.FromArgb(180, 180, 180);
        }
    }

    private void ConfigureForm()
    {
        // Configuración para centrar y hacer modal
        this.StartPosition = FormStartPosition.CenterScreen;
        this.TopMost = true;
        this.ShowInTaskbar = true;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        
        // Configuración de botones
        this.AcceptButton = _buttonSelect;
        this.CancelButton = _buttonCancel;
        
        // Asegurar que el formulario se mantenga en primer plano
        this.WindowState = FormWindowState.Normal;
        this.BringToFront();
        this.Focus();
    }

    private void LoadDevicesOptimized()
    {
        _listBoxDevices.BeginUpdate();
        _listBoxDevices.Items.Clear();
        
        _labelCount.Text = $" Dispositivos encontrados: {_currentDevices.Count}";
        
        if (_currentDevices.Count == 0)
        {
            _listBoxDevices.Items.Add(" No se encontraron dispositivos de escaneo");
            _buttonSelect.Enabled = false;
            _progressBar.Visible = false;
            _sortedDevices.Clear();
            
            _labelStatus.Text = " Conecta un escáner USB o de red al ordenador y presiona 'Actualizar'";
            _labelStatus.ForeColor = Color.FromArgb(255, 140, 0);
            _labelCount.Text = " No se detectaron dispositivos de escáner en el sistema";
            _labelCount.ForeColor = Color.FromArgb(220, 20, 60);
            
            MessageBox.Show(this,
                "No hay dispositivos de escáner conectados al ordenador.\n\n" +
                "Por favor:\n" +
                "• Conecta el escáner via USB\n" +
                "• Verifica que esté encendido\n" +
                "• Presiona 'Actualizar' después de conectar",
                "No hay dispositivos conectados",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            
            return;
        }

        // Ordenar dispositivos
        _sortedDevices = _currentDevices
            .OrderBy(d => d.Type == ScannerType.TWAIN ? 0 : 1)
            .ThenBy(d => d.Name)
            .ToList();

        // Poblar la lista
        foreach (var device in _sortedDevices)
        {
            _listBoxDevices.Items.Add(device);
        }

        _listBoxDevices.EndUpdate();

        // Seleccionar el primer dispositivo
        if (_listBoxDevices.Items.Count > 0)
        {
            _listBoxDevices.SelectedIndex = 0;
            _progressBar.Visible = false;
            
            if (_sortedDevices.Count == 1)
            {
                _labelCount.Text = " 1 dispositivo disponible";
                _labelStatus.Text = " Dispositivo listo para escanear";
                _labelStatus.ForeColor = Color.FromArgb(40, 167, 69);
            }
            else
            {
                _labelCount.Text = $" {_sortedDevices.Count} dispositivos disponibles";
                _labelStatus.Text = " Selecciona el dispositivo deseado";
                _labelStatus.ForeColor = Color.FromArgb(0, 120, 215);
            }
            
            _labelCount.ForeColor = Color.FromArgb(40, 167, 69);
        }
        else
        {
            _buttonSelect.Enabled = false;
            _progressBar.Visible = false;
            _labelStatus.Text = " No hay dispositivos disponibles";
            _labelStatus.ForeColor = Color.FromArgb(220, 20, 60);
        }

        _listBoxDevices.KeyDown += ListBoxDevices_KeyDown;
        this.KeyPreview = true;
        this.KeyDown += Form_KeyDown;
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshDevicesCallback == null)
        {
            _labelStatus.Text = " Función de actualización no disponible";
            _labelStatus.ForeColor = Color.FromArgb(220, 20, 60);
            return;
        }

        try
        {
            _buttonRefresh.Enabled = false;
            _buttonSelect.Enabled = false;
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            
            _labelStatus.Text = " Buscando dispositivos recién conectados...";
            _labelStatus.ForeColor = Color.FromArgb(0, 120, 215);
            
            _listBoxDevices.BeginUpdate();
            _listBoxDevices.Items.Clear();
            _listBoxDevices.Items.Add("🔍 Detectando dispositivos nuevos...");
            _listBoxDevices.EndUpdate();

            // Timeout reducido para refresh desde UI
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            
            var refreshTask = _refreshDevicesCallback();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
            
            var completedTask = await Task.WhenAny(refreshTask, timeoutTask);
            
            List<ScannerDevice> updatedDevices;
            
            if (completedTask == refreshTask)
            {
                updatedDevices = await refreshTask ?? new List<ScannerDevice>();
            }
            else
            {
                _labelStatus.Text = " Búsqueda tomó demasiado tiempo - usando dispositivos previos";
                _labelStatus.ForeColor = Color.FromArgb(255, 140, 0);
                updatedDevices = _currentDevices;
            }
            
            _currentDevices = updatedDevices;
            LoadDevicesOptimized();
            
            // Mostrar resultado
            if (_currentDevices.Count > 0)
            {
                var newCount = _currentDevices.Count - _initialDevices.Count;
                if (newCount > 0)
                {
                    _labelStatus.Text = $"🎉 ¡{newCount} dispositivo(s) nuevo(s) encontrado(s)!";
                    _labelStatus.ForeColor = Color.FromArgb(40, 167, 69);
                }
                else
                {
                    _labelStatus.Text = " Lista actualizada correctamente";
                    _labelStatus.ForeColor = Color.FromArgb(40, 167, 69);
                }
            }
            else
            {
                _labelStatus.Text = " No se encontraron dispositivos";
                _labelStatus.ForeColor = Color.FromArgb(255, 140, 0);
            }
        }
        catch (Exception ex)
        {
            _labelStatus.Text = $" Error actualizando: {ex.Message}";
            _labelStatus.ForeColor = Color.FromArgb(220, 20, 60);
            
            _currentDevices = new List<ScannerDevice>(_initialDevices);
            LoadDevicesOptimized();
        }
        finally
        {
            _buttonRefresh.Enabled = true;
            _progressBar.Visible = false;
        }
    }

    private string GetTypeIcon(ScannerType type)
    {
        return type switch
        {
            ScannerType.TWAIN => "",
            ScannerType.WIA => "",
            _ => ""
        };
    }

    private void SelectDevice()
    {
        if (_listBoxDevices.SelectedIndex >= 0 && _listBoxDevices.SelectedIndex < _sortedDevices.Count)
        {
            var selectedDevice = _sortedDevices[_listBoxDevices.SelectedIndex];
            SelectedDevice = selectedDevice;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    private void CancelSelection()
    {
        SelectedDevice = null;
        this.DialogResult = DialogResult.Cancel;
        this.Close();
    }

    private void ListBoxDevices_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.Handled = true;
                SelectDevice();
                break;
            case Keys.Escape:
                e.Handled = true;
                CancelSelection();
                break;
            case Keys.F5:
                e.Handled = true;
                _ = RefreshDevicesAsync();
                break;
        }
    }

    private void Form_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                if (_buttonSelect.Enabled)
                {
                    e.Handled = true;
                    SelectDevice();
                }
                break;
            case Keys.Escape:
                e.Handled = true;
                CancelSelection();
                break;
            case Keys.F5:
                if (_buttonRefresh.Enabled)
                {
                    e.Handled = true;
                    _ = RefreshDevicesAsync();
                }
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (this.DialogResult == DialogResult.None)
        {
            this.DialogResult = DialogResult.Cancel;
            SelectedDevice = null;
        }
        
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        
        // Asegurar que el formulario esté centrado y en primer plano
        this.CenterToScreen();
        this.TopMost = true;
        this.BringToFront();
        this.Activate();
        
        // Dar foco al ListBox
        _listBoxDevices.Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        
        // Mantener el formulario en primer plano cuando se activa
        this.TopMost = true;
        this.BringToFront();
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(value);
        
        if (value)
        {
            // Asegurar que el formulario esté en primer plano al hacerse visible
            this.TopMost = true;
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Focus();
        }
    }
}