using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tesseract;

namespace LolTracker
{
    public partial class MainForm : Form
    {
        private Timer lolCheckTimer;
        private DirectXManager dxManager;
        private TesseractEngine ocrEng;

        private bool isProcessing = false;
        private Timer cooloffTimer = null;
        private int currentCS;
        private int currentMin;
        private int currentSec;
        private int numErrors = 0;
        private Regex ocrRegex;

        public MainForm()
        {
            InitializeComponent();
            dxManager = new DirectXManager();
            lolCheckTimer = new Timer();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            dxManager.Init(Handle);

            currentCS = 0;
            currentMin = 0;
            currentSec = 0;
            ocrRegex = new Regex(@"\w[ ](\d+)[ ][0|O][ ](\d+):(\d+)");

            // start checking to see if LoL is running
            lolCheckTimer.Tick += CheckLoLStatus;
            lolCheckTimer.Interval = 5000;
            lolCheckTimer.Start();

            // Setup Tesseract
            ocrEng = new TesseractEngine(@"./tessdata", "eng", EngineMode.Default);
            ocrEng.SetVariable("tessedit_char_whitelist", " AO:012345789");
        }

        private void CheckLoLStatus(object sender, EventArgs e)
        {
            //var isRunning = IsProcessRunningByWindowTitle("Google Chrome");
            var isRunning = IsProcessRunningByWindowTitle("League of Legends (TM) Client");
            if (isRunning)
            {
                lolStatusLabel.Text = "Running";
                if (!dxManager.IsRecording)
                    dxManager.StartRecording(() => onFrameUpdate());
            } else
            {
                lolStatusLabel.Text = "Not Running";
                if (dxManager.IsRecording)
                    dxManager.StopRecording();
                currentCS = 0;
                currentMin = 0;
                currentSec = 0;
            }
        }

        private async void onFrameUpdate()
        {
            Console.WriteLine("onFrameUpdate");
            Console.WriteLine("Removing frame update info from queue");
            var update = dxManager.GetProcessor().Take();
            // LoL updates by updating the entire screen at the same time, so anything else we know isn't actually LoL
            if (update.DirtyRects.Length == 1 && update.DirtyRects[0].width == update.LastAcquiredFrame.Width && !isProcessing)
            {
                try {
                    // First off trim to just the top-right corner
                    var trimmed = update.LastAcquiredFrame.Clone(new Rectangle((int)(update.LastAcquiredFrame.Width * 0.9), 0, (int)(update.LastAcquiredFrame.Width * 0.1), 40), update.LastAcquiredFrame.PixelFormat);

                    // Then let's make it a Pix and easier to OCR
                    var img = PixConverter.ToPix(trimmed);
                    img = img.ConvertRGBToGray();
                    //img = img.BinarizeOtsuAdaptiveThreshold(24, 24, 8, 8, 0.0f);
                    //img.Save("analysis.png");
                    // Then the action
                    isProcessing = true;

                    var page = ocrEng.Process(img, PageSegMode.SingleLine);
                    var allText = page.GetText();

                    if (ocrRegex.IsMatch(allText))
                    {
                        var matchCollection = ocrRegex.Match(allText);
                        var csString = matchCollection.Groups[1].Value;
                        var minString = matchCollection.Groups[2].Value;
                        var secString = matchCollection.Groups[3].Value;

                        int cs;
                        int min;
                        int sec;

                        bool csIsInt = int.TryParse(csString, out cs);
                        bool minIsInt = int.TryParse(minString, out min);
                        bool secIsInt = int.TryParse(minString, out sec);

                        if (!(csIsInt && minIsInt && secIsInt))
                        {
                            Console.WriteLine("Unable to parse int values from {0}", allText);
                            trimmed.Save(string.Format("errors/err_{0}.png", numErrors));
                            numErrors += 1;
                        } else
                        {
                            if (cs != currentCS)
                            {
                                currentCS = cs;
                                currentMin = min;
                                currentSec = sec;

                                Console.WriteLine("{0}:{1} - {2}cs", currentMin, currentSec, currentCS);
                                currentCsLabel.Invoke((MethodInvoker)(() => {
                                    currentCsLabel.Text = "" + currentCS;
                                    currentTimeLabel.Text = string.Format("{0}:{1}", currentMin, currentSec);
                                }));
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("Unable to match regex against {0}", allText);
                    }

                    page.Dispose();
                    img.Dispose();
                    trimmed.Dispose();
                    isProcessing = false;
                } catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            await Task.Delay(2000);
            dxManager.GetRecorder().Add(update);
        }

        public bool IsProcessRunningByWindowTitle(string name)
        {
            foreach (Process clsProcess in Process.GetProcesses())
            {
                if (clsProcess.MainWindowTitle.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
