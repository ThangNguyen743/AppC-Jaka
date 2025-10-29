using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using jakaApi;
using jkType;
using System.Collections.Generic;
using System.Linq;

namespace JAKA_APP
{
    public partial class MainWindow : Window
    {
        private int rshd = 0;
        private bool connected = false;
        private bool enabled = false;
        private bool dark = false;
        private double speed = 20.0;
        private readonly double d2r = Math.PI / 180.0;

        private readonly double[] jointMin = { -360, -50, -155, -85, -360, -360 };
        private readonly double[] jointMax = { 360, 230, 155, 265, 360, 360 };

        private DispatcherTimer syncTimer;
        private bool isPaused = false;
        private bool isRunningSequence = false;

        public MainWindow()
        {
            InitializeComponent();
            SetEnvironment();
            UpdateUIState();
            ApplyTheme();
        }

        #region === Environment & Theme ===
        private void SetEnvironment()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dllPath = FindFileRecursive(baseDir, "jakaAPI.dll");
                if (dllPath != null)
                {
                    string folder = Path.GetDirectoryName(dllPath)!;
                    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    Environment.SetEnvironmentVariable("PATH", folder + ";" + path);
                    Log($" PATH += {folder}");
                }
                else
                {
                    MessageBox.Show("Không tìm thấy jakaAPI.dll!", "Thiếu DLL", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi SetEnvironment: {ex.Message}");
            }
        }

        private string? FindFileRecursive(string root, string file)
        {
            foreach (var f in Directory.GetFiles(root, file, SearchOption.AllDirectories)) return f;
            return null;
        }

        private void Log(string msg)
        {
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            txtLog.ScrollToEnd();
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            dark = !dark;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            if (dark)
            {
                RootGrid.Background = new SolidColorBrush(Color.FromRgb(30, 32, 34));
                Foreground = Brushes.WhiteSmoke;
                txtLog.Background = new SolidColorBrush(Color.FromRgb(25, 25, 25));
                txtLog.Foreground = Brushes.Lime;
                btnTheme.Content = "Light Mode";
            }
            else
            {
                RootGrid.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                Foreground = Brushes.Black;
                txtLog.Background = new SolidColorBrush(Color.FromRgb(20, 20, 20));
                txtLog.Foreground = Brushes.Lime;
                btnTheme.Content = "Dark Mode";
            }
        }
        #endregion

        #region === Connection & Power ===
        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            string ip = txtRobotIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show("Nhập IP robot!");
                return;
            }

            Log($"Kết nối tới {ip} ...");
            await Task.Run(() =>
            {
                int ret = jakaAPI.create_handler(ip, ref rshd);
                Dispatcher.Invoke(() =>
                {
                    connected = ret == 0;
                    Log(connected ? "Connect OK" : $"Connect lỗi ({ret})");
                    UpdateUIState();
                });
            });
        }

        private void PowerOn_Click(object sender, RoutedEventArgs e)
        {
            if (!connected) return;
            int ret = jakaAPI.power_on(ref rshd);
            Log(ret == 0 ? "Power On OK" : $"Power On lỗi ({ret})");
        }

        private void PowerOff_Click(object sender, RoutedEventArgs e)
        {
            if (!connected) return;
            jakaAPI.disable_robot(ref rshd);
            jakaAPI.power_off(ref rshd);
            enabled = false;
            StopJointSync();
            Log("🔴 Power Off.");
            UpdateUIState();
        }

        private void Enable_Click(object sender, RoutedEventArgs e)
        {
            if (!connected) return;
            int ret = jakaAPI.enable_robot(ref rshd);
            enabled = ret == 0;
            if (enabled) StartJointSync();
            Log(enabled ? "Enable OK" : $"Enable lỗi ({ret})");
            UpdateUIState();
        }

        private void Disable_Click(object sender, RoutedEventArgs e)
        {
            if (!connected) return;
            int ret = jakaAPI.disable_robot(ref rshd);
            if (ret == 0)
            {
                enabled = false;
                StopJointSync();
                Log("Disable OK");
            }
            else Log($"Disable lỗi ({ret})");
            UpdateUIState();
        }

        private void UpdateUIState()
        {
            bool canMove = connected && enabled;

            btnPowerOn.IsEnabled = connected;
            btnEnable.IsEnabled = connected && !enabled;
            btnDisable.IsEnabled = connected && enabled;
            btnPowerOff.IsEnabled = connected;
            btnMoveAll.IsEnabled = canMove;
            btnMoveHome.IsEnabled = canMove;

            for (int i = 1; i <= 6; i++)
                ((Button)FindName($"btnJ{i}")).IsEnabled = canMove;

            if (!connected)
                Title = "🔴 Disconnected - JAKA Control";
            else if (connected && !enabled)
                Title = "🟡 Connected (Disabled) - JAKA Control";
            else
                Title = "🟢 Connected & Enabled - JAKA Control";
        }
        #endregion

        #region === Joint Control ===
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            speed = e.NewValue;
            if (lblSpeed != null) lblSpeed.Content = $"{speed:0}%";
        }

        private float SpeedRatio() => (float)(Math.Max(5, Math.Min(100, speed)) / 100.0);

        private bool CheckJointLimit(int index1based, double degree)
        {
            int i = index1based - 1;
            if (degree < jointMin[i] || degree > jointMax[i])
            {
                MessageBox.Show($"Joint {index1based} vượt giới hạn [{jointMin[i]}, {jointMax[i]}]°");
                return false;
            }
            return true;
        }

        private async Task SmoothMoveJoint(int index, double targetDeg, double duration = 1.0)
        {
            var jNow = new JKTYPE.JointValue { jVal = new double[6] };
            int ret = jakaAPI.get_joint_position(ref rshd, ref jNow);
            if (ret != 0) return;

            double currentDeg = jNow.jVal[index] * 180 / Math.PI;
            double stepDeg = (targetDeg - currentDeg) / 20.0;
            double stepTime = duration / 20.0;

            for (int k = 1; k <= 20; k++)
            {
                double nextDeg = currentDeg + stepDeg * k;
                var j = new JKTYPE.JointValue { jVal = (double[])jNow.jVal.Clone() };
                j.jVal[index] = nextDeg * Math.PI / 180.0;

                await Task.Run(() =>
                {
                    jakaAPI.joint_move(ref rshd, ref j, JKTYPE.MoveMode.ABS, true, SpeedRatio());
                });
                await Task.Delay(TimeSpan.FromSeconds(stepTime));
            }
        }

        private async void MoveJoint_Click(object sender, RoutedEventArgs e)
        {
            if (!connected || !enabled) return;
            if (sender is not Button btn || btn.Tag == null) return;

            int j = int.Parse(btn.Tag.ToString());
            TextBox tb = (TextBox)FindName($"txtJ{j}");
            if (!double.TryParse(tb.Text, out double deg))
            {
                MessageBox.Show($"Joint {j} không hợp lệ!");
                return;
            }
            if (!CheckJointLimit(j, deg)) return;

            Log($"Smooth Joint {j} → {deg:0.0}° | speed={SpeedRatio():0.00}");
            await SmoothMoveJoint(j - 1, deg, 1.0);
        }

        private async void MoveAllJoints_Click(object sender, RoutedEventArgs e)
        {
            if (!connected || !enabled) return;

            var j = new JKTYPE.JointValue { jVal = new double[6] };
            for (int i = 0; i < 6; i++)
            {
                if (!double.TryParse(((TextBox)FindName($"txtJ{i + 1}")).Text, out double deg))
                {
                    MessageBox.Show($"Joint {i + 1} không hợp lệ!");
                    return;
                }
                if (!CheckJointLimit(i + 1, deg)) return;
                j.jVal[i] = deg * d2r;
            }

            Log($"Move ALL joints | speed={SpeedRatio():0.00}");
            await Task.Run(() =>
            {
                int ret = jakaAPI.joint_move(ref rshd, ref j, JKTYPE.MoveMode.ABS, true, SpeedRatio());
                Dispatcher.Invoke(() => Log(ret == 0 ? "OK" : $"lỗi ({ret})"));
            });
        }

        private async void MoveHome_Click(object sender, RoutedEventArgs e)
        {
            if (!connected || !enabled) return;

            var home = new JKTYPE.JointValue { jVal = new double[6] { 0, 0, 0, 0, 0, 0 } };
            Log("Move to Home position...");
            await Task.Run(() =>
            {
                int ret = jakaAPI.joint_move(ref rshd, ref home, JKTYPE.MoveMode.ABS, true, SpeedRatio());
                Dispatcher.Invoke(() => Log(ret == 0 ? "Home OK" : $"lỗi ({ret})"));
            });

            for (int i = 1; i <= 6; i++)
                ((TextBox)FindName($"txtJ{i}")).Text = "0.0";
        }
        #endregion

        #region === Auto Joint Sync ===
        private void StartJointSync()
        {
            if (syncTimer != null) return;
            syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            syncTimer.Tick += async (s, e) =>
            {
                if (!connected || !enabled) return;
                var joint = new JKTYPE.JointValue { jVal = new double[6] };
                int ret = await Task.Run(() => jakaAPI.get_joint_position(ref rshd, ref joint));
                if (ret == 0)
                {
                    for (int i = 0; i < 6; i++)
                        ((TextBox)FindName($"txtJ{i + 1}")).Text = $"{joint.jVal[i] * 180 / Math.PI:0.0}";
                }
            };
            syncTimer.Start();
            Log("Joint sync started.");
        }

        private void StopJointSync()
        {
            syncTimer?.Stop();
            syncTimer = null;
            Log("Joint sync stopped.");
        }
        #endregion

        #region === Teach & Playback (Joint) ===
        private class TaughtPoint
        {
            public string Name { get; set; } = "";
            public double[] JointRad { get; set; } = new double[6];
            public double DelaySec { get; set; } = 0;
        }

        private List<TaughtPoint> taughtPointsAdv = new();
        private bool isPausedTeach = false;
        private bool isRunningTeach = false;

        // === Record ===
        private async void RecordPointAdv_Click(object sender, RoutedEventArgs e)
        {
            if (!connected) return;
            string name = string.IsNullOrWhiteSpace(txtPointName.Text)
                ? $"Point{taughtPointsAdv.Count + 1}" : txtPointName.Text;
            if (!double.TryParse(txtDelay.Text, out double delaySec)) delaySec = 0;

            var j = new JKTYPE.JointValue { jVal = new double[6] };
            await Task.Run(() =>
            {
                int ret = jakaAPI.get_joint_position(ref rshd, ref j);
                if (ret == 0)
                {
                    var point = new TaughtPoint
                    {
                        Name = name,
                        JointRad = (double[])j.jVal.Clone(),
                        DelaySec = delaySec
                    };
                    taughtPointsAdv.Add(point);
                    Dispatcher.Invoke(() =>
                    {
                        lstPointsAdv.Items.Add($"[{name}] ({string.Join(", ", Array.ConvertAll(j.jVal, rad => $"{rad * 180 / Math.PI:0.0}°"))}) Delay: {delaySec:0.0}s");
                        Log($"Ghi điểm '{name}' (Delay {delaySec:0.0}s)");
                        txtPointName.Clear();
                    });
                }
            });
        }

        // === Run Sequence ===
        private async void RunSequenceAdv_Click(object sender, RoutedEventArgs e)
        {
            if (!connected || !enabled || taughtPointsAdv.Count == 0)
            {
                MessageBox.Show("Chưa có điểm nào để chạy!");
                return;
            }

            int loopCount = int.TryParse(txtLoopCount.Text, out int val) ? val : 1;
            bool infinite = chkLoopInfinite.IsChecked == true;
            isRunningTeach = true;

            Log($"Bắt đầu chạy {(infinite ? "vô hạn" : $"{loopCount}")} vòng...");
            int loop = 0;

            while (isRunningTeach && (infinite || loop < loopCount))
            {
                loop++;
                foreach (var p in taughtPointsAdv)
                {
                    while (isPausedTeach) await Task.Delay(200);
                    var j = new JKTYPE.JointValue { jVal = (double[])p.JointRad.Clone() };

                    Log($"Điểm: {p.Name}");
                    await Task.Run(() =>
                    {
                        int ret = jakaAPI.joint_move(ref rshd, ref j, JKTYPE.MoveMode.ABS, true, SpeedRatio());
                        Dispatcher.Invoke(() => Log(ret == 0 ? $" {p.Name} OK" : $" Lỗi tại {p.Name}"));
                    });

                    if (p.DelaySec > 0) await Task.Delay(TimeSpan.FromSeconds(p.DelaySec));
                    if (!isRunningTeach) break;
                }
            }
            Log("Chuỗi hoàn tất.");
            isRunningTeach = false;
        }

        private void PauseSequence_Click(object sender, RoutedEventArgs e)
        {
            isPausedTeach = true;
            Log("Tạm dừng chuỗi.");
        }

        private void ResumeSequence_Click(object sender, RoutedEventArgs e)
        {
            isPausedTeach = false;
            Log("Tiếp tục chuỗi.");
        }
        #endregion

        #region === Cartesian Control (XYZ RX RY RZ) ===
        private async void GetPose_Click(object sender, RoutedEventArgs e)
        {
            if (!connected || !enabled)
            {
                MessageBox.Show("Robot chưa kết nối hoặc chưa Enable!");
                return;
            }

            var pose = new JKTYPE.CartesianPose();
            await Task.Run(() =>
            {
                int ret = jakaAPI.get_tcp_position(ref rshd, ref pose);
                Dispatcher.Invoke(() =>
                {
                    if (ret == 0)
                    {
                        txtX.Text = (pose.tran.x *1000 ).ToString("0.0");
                        txtY.Text = (pose.tran.y * 1000).ToString("0.0");
                        txtZ.Text = (pose.tran.z * 1000).ToString("0.0");
                        txtRX.Text = (pose.rpy.rx * 180 / Math.PI).ToString("0.0");
                        txtRY.Text = (pose.rpy.ry * 180 / Math.PI).ToString("0.0");
                        txtRZ.Text = (pose.rpy.rz * 180 / Math.PI).ToString("0.0");
                        Log("Đọc TCP Pose thành công.");
                        Log($"TCP Pose: X={txtX.Text}mm, Y={txtY.Text}mm, Z={txtZ.Text}mm");
                    }
                    else Log($"Lỗi get_tcp_position ({ret})");
                });
            });
        }

        private async void MovePose_Click(object sender, RoutedEventArgs e)
        {
            if (!connected || !enabled)
            {
                MessageBox.Show("Robot chưa sẵn sàng!");
                return;
            }

            try
            {
                // --- Lấy giá trị từ textbox ---
                double x = double.Parse(txtX.Text) / 1000.0; // mm → m
                double y = double.Parse(txtY.Text) / 1000.0;
                double z = double.Parse(txtZ.Text) / 1000.0;

                double rx = double.Parse(txtRX.Text) * Math.PI / 180.0;
                double ry = double.Parse(txtRY.Text) * Math.PI / 180.0;
                double rz = double.Parse(txtRZ.Text) * Math.PI / 180.0;

                // --- Tạo pose theo SDK ---
                var pose = new JKTYPE.CartesianPose
                {
                    tran = new JKTYPE.CartesianTran { x = x, y = y, z = z },
                    rpy = new JKTYPE.Rpy { rx = rx, ry = ry, rz = rz }
                };

                // --- Quy đổi speed (%) thành vận tốc thực tế ---
                float v = (float)(100 * SpeedRatio());  // 0.25 m/s là tốc độ tối đa đề xuất
                Log($"Move TCP → X={x * 1000:0.0} Y={y * 1000:0.0} Z={z * 1000:0.0} | Speed={v:0.00} m/s");

                // --- Thực thi lệnh di chuyển ---
                await Task.Run(() =>
                {
                    int ret = jakaAPI.linear_move(ref rshd, ref pose, JKTYPE.MoveMode.ABS, true, v);
                    Dispatcher.Invoke(() =>
                    {
                        if (ret == 0)
                            Log($"Cartesian Move thành công (Speed={v:0.00} m/s)");
                        else
                            Log($"Lỗi khi di chuyển (Code {ret})");
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi dữ liệu: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion
        // === Save / Load / Clear Teach Points ===
        private void SavePointsAdv_Click(object sender, RoutedEventArgs e)
        {
            if (taughtPointsAdv.Count == 0)
            {
                MessageBox.Show("Chưa có điểm nào để lưu!");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JAKA Points (*.txt)|*.txt",
                FileName = "jaka_points.txt"
            };
            if (dlg.ShowDialog() == true)
            {
                using (var sw = new StreamWriter(dlg.FileName))
                {
                    foreach (var p in taughtPointsAdv)
                    {
                        string data = string.Join(",", p.JointRad.Select(v => v.ToString("F6")));
                        sw.WriteLine($"{p.Name}|{p.DelaySec}|{data}");
                    }
                }
                Log($"Đã lưu {taughtPointsAdv.Count} điểm ra file {dlg.FileName}");
            }
        }

        private void LoadPointsAdv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JAKA Points (*.txt)|*.txt"
            };
            if (dlg.ShowDialog() == true)
            {
                taughtPointsAdv.Clear();
                lstPointsAdv.Items.Clear();

                foreach (var line in File.ReadAllLines(dlg.FileName))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3) continue;

                    string name = parts[0];
                    double delay = double.Parse(parts[1]);
                    double[] joints = parts[2].Split(',').Select(double.Parse).ToArray();

                    taughtPointsAdv.Add(new TaughtPoint { Name = name, JointRad = joints, DelaySec = delay });
                    lstPointsAdv.Items.Add($"{name} | Delay={delay:0.0}s");
                }

                Log($"Đã tải {taughtPointsAdv.Count} điểm từ file {dlg.FileName}");
            }
        }

        private void ClearPointsAdv_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Xóa toàn bộ danh sách điểm?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                taughtPointsAdv.Clear();
                lstPointsAdv.Items.Clear();
                Log("Đã xóa toàn bộ danh sách điểm.");
            }
        }
        // === Dừng / Tiếp tục Auto Sync khi người dùng nhập Joint ===
private void JointTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (syncTimer != null)
            {
                syncTimer.Stop();
                Log("Dừng auto sync khi nhập Joint.");
            }
        }

        private void JointTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (enabled && connected)
            {
                syncTimer?.Start();
                Log("Tiếp tục auto sync sau khi nhập xong.");
            }
        }

    }
}
