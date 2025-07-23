using System.ComponentModel;

namespace ScannerWebSocketFormService;

partial class UnifiedDeviceSelectorForm
{
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }
    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        this.Text = "Seleccionar Dispositivo de Escaneo";
        this.Size = new Size(520, 380); 
        this.MinimumSize = new Size(520, 380);
        this.MaximumSize = new Size(520, 380);
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.Font = new Font("Segoe UI", 9F);

        // Panel principal
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5, // Reducido de 6 a 5 (sin panel de conectividad)
            Padding = new Padding(12),
            BackColor = Color.White
        };

        // Configurar filas
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F)); // Título
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25F)); // Contador
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Lista
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F)); // Status
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 75F)); // Botones

        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        // ===== TÍTULO =====
        _labelTitle = new Label
        {
            Text = "🖨️ Selecciona un dispositivo para escanear",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(64, 64, 64),
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoSize = false
        };

        // ===== CONTADOR =====
        _labelCount = new Label
        {
            Text = " Dispositivos encontrados: 0",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(100, 100, 100),
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoSize = false
        };

        // ===== LISTA DE DISPOSITIVOS =====
        var listPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            Padding = new Padding(1)
        };

        _listBoxDevices = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F),
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(64, 64, 64),
            ItemHeight = 24,
            DrawMode = DrawMode.OwnerDrawFixed,
            SelectionMode = SelectionMode.One,
            IntegralHeight = false
        };

        _listBoxDevices.DrawItem += ListBoxDevices_DrawItem;
        _listBoxDevices.SelectedIndexChanged += ListBoxDevices_SelectedIndexChanged;

        listPanel.Controls.Add(_listBoxDevices);

        // ===== PROGRESS BAR - CORREGIDO PARA QUE NO CUBRA TODO =====
        _progressBar = new ProgressBar
        {
            Size = new Size(400, 6), // Tamaño fijo pequeño
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 50,
            Visible = false,
            BackColor = Color.FromArgb(0, 123, 255),
            ForeColor = Color.FromArgb(0, 123, 255),
            Anchor = AnchorStyles.None // Para centrarlo
        };

        // Panel contenedor para el progress bar centrado
        var progressPanel = new Panel
        {
            Size = new Size(400, 20),
            BackColor = Color.Transparent,
            Visible = false
        };
        
        progressPanel.Controls.Add(_progressBar);
        _progressBar.Location = new Point(0, 7); // Centrado verticalmente en el panel

        listPanel.Controls.Add(progressPanel);
        progressPanel.BringToFront();
        // Centrar el panel del progress bar sobre la lista
        progressPanel.Location = new Point(
            (listPanel.Width - progressPanel.Width) / 2,
            (listPanel.Height - progressPanel.Height) / 2
        );

        // ===== STATUS =====
        _labelStatus = new Label
        {
            Text = "Selecciona un dispositivo de la lista",
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(100, 100, 100),
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            AutoSize = false
        };

        // ===== BOTONES =====
        _buttonPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };

        var buttonLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent
        };

        buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // Espacio
        buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));  // Actualizar
        buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));  // Scanear
        buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));  // Cancelar

        _buttonRefresh = new Button
        {
            Text = "🔄 Actualizar",
            Font = new Font("Segoe UI", 9F),
            BackColor = Color.FromArgb(108, 117, 125),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(85, 35),
            Anchor = AnchorStyles.Right,
            Cursor = Cursors.Hand
        };
        _buttonRefresh.FlatAppearance.BorderSize = 0;
        _buttonRefresh.Click += async (s, e) => await RefreshDevicesAsync();

        // ===== BOTÓN CAMBIADO A "SCANEAR" =====
        _buttonSelect = new Button
        {
            Text = "🖨️ Scanear", // CAMBIADO DE "✅ Seleccionar" A "🖨️ Scanear"
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(85, 35),
            Anchor = AnchorStyles.Right,
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _buttonSelect.FlatAppearance.BorderSize = 0;
        _buttonSelect.Click += (s, e) => SelectDevice();

        _buttonCancel = new Button
        {
            Text = "❌ Cancelar",
            Font = new Font("Segoe UI", 9F),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(85, 35),
            Anchor = AnchorStyles.Right,
            Cursor = Cursors.Hand
        };
        _buttonCancel.FlatAppearance.BorderSize = 0;
        _buttonCancel.Click += (s, e) => CancelSelection();

        // Agregar espacio vacío y botones
        buttonLayout.Controls.Add(new Panel(), 0, 0);
        buttonLayout.Controls.Add(_buttonRefresh, 1, 0);
        buttonLayout.Controls.Add(_buttonSelect, 2, 0);
        buttonLayout.Controls.Add(_buttonCancel, 3, 0);

        _buttonPanel.Controls.Add(buttonLayout);

        // ===== AGREGAR TODO AL PANEL PRINCIPAL =====
        mainPanel.Controls.Add(_labelTitle, 0, 0);
        mainPanel.Controls.Add(_labelCount, 0, 1);
        mainPanel.Controls.Add(listPanel, 0, 2);
        mainPanel.Controls.Add(_labelStatus, 0, 3);
        mainPanel.Controls.Add(_buttonPanel, 0, 4);

        // Agregar solo el panel principal (el progress bar ya está dentro del listPanel)
        this.Controls.Add(mainPanel);

        // Evento para recentrar el progress bar cuando la ventana cambie de tamaño
        listPanel.Resize += (s, e) => {
            if (progressPanel.Parent != null)
            {
                progressPanel.Location = new Point(
                    (listPanel.Width - progressPanel.Width) / 2,
                    (listPanel.Height - progressPanel.Height) / 2
                );
            }
        };
    }
    
    #endregion
}