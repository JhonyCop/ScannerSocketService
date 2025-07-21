using Microsoft.Win32;

namespace ScannerWebSocketFormService
{
    static class Program
    {
        /// <summary>
        /// Punto de entrada principal de la aplicación.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Agregar al inicio de Windows
             AgregarAlInicio();

            // Configuración estándar del formulario
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }

        static void AgregarAlInicio()
        {
            try
            {
                string nombreApp = "ScannerWebSocketFormService";
                string rutaEjecutable = Application.ExecutablePath;

                using RegistryKey clave = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                if (clave != null && clave.GetValue(nombreApp) == null)
                {
                    clave.SetValue(nombreApp, $"\"{rutaEjecutable}\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al intentar registrar la app en el inicio de Windows:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}