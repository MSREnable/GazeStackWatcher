using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Input.Preview;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace GazeStackWatcher
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        const int MaxListEntries = 1000;
        readonly TimeSpan InputPauseTimeout = TimeSpan.FromSeconds(30);

        readonly GazeDeviceWatcherPreview _watcher = GazeInputSourcePreview.CreateWatcher();
        int _deviceCount;

        bool _isSourceHoooked;

        readonly GazeInputSourcePreview _source = GazeInputSourcePreview.GetForCurrentView();

        Point? _lastPosition;
        TimeSpan _lastTimestamp;
        TimeSpan _startTimestamp;
        TimeSpan _lastTickCount;

        int _noPositionCount;
        int _newPositionCount;
        int _repeatPositionCount;

        int _blickCount;

        bool _isEntered;
        bool _isFirstReportExpected;
        bool _isReporting;
        bool _isReportPreviewShown;

        TimeSpan _minDelta;
        TimeSpan _maxDelta;


        readonly DispatcherTimer _reportingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        IStorageFile _logFile;
        readonly SemaphoreSlim _logSemaphore = new SemaphoreSlim(1);
        readonly List<string> _logBuffer = new List<string>();

        public MainPage()
        {
            this.InitializeComponent();

            ThePicker.FileTypeChoices.Add("Log file", new List<string> { ".log" });

            Loaded += async (s, e) =>
              {
                  _logFile = await ThePicker.PickSaveFileAsync();

                  _reportingTimer.Tick += OnReportingTick;

                  _watcher.Added += OnDeviceAdded;
                  _watcher.EnumerationCompleted += OnDeviceEnumerationCompleted;
                  _watcher.Updated += OnDeviceUpdated;
                  _watcher.Removed += OnDeviceRemoved;
                  _watcher.Start();

                  Log("Started");
              };
        }

        private string TimestampString => DateTimeOffset.Now.ToString("dd/MM/yy HH:mm:ss.ff");

        private void ListInsertHead(string text)
        {
            while (MaxListEntries <= TheListBox.Items.Count)
            {
                TheListBox.Items.RemoveAt(MaxListEntries - 1);
            }

            TheListBox.Items.Insert(0, text);
        }

        private void ListReplaceHead(string text)
        {
            TheListBox.Items[0] = text;
        }

        private void OnReportingTick(object sender, object e)
        {
            if (_isReportPreviewShown)
            {
                ListReplaceHead(ReportingText);
            }
            else
            {
                ListInsertHead(ReportingText);
                _isReportPreviewShown = true;
            }

            var pauseTheshold = TimeSpan.FromMilliseconds(Environment.TickCount) - InputPauseTimeout;

            if (_lastTickCount < pauseTheshold)
            {
                Log($"Input paused longer than {InputPauseTimeout:G}");
            }
        }

        private string ReportingText
        {
            get
            {
                return $"{TimestampString}\tReported total of {_noPositionCount + _newPositionCount + _repeatPositionCount} points, {_repeatPositionCount} repeated positions, {_blickCount} blinks and {_noPositionCount} null positions between {_startTimestamp:G} to {_lastTickCount:G}";
            }
        }

        private void Log(string message)
        {
            if (_isReporting)
            {
                var text = ReportingText;

                if (_isReportPreviewShown)
                {
                    ListReplaceHead(text);
                    _isReportPreviewShown = false;
                }
                else
                {
                    ListInsertHead(text);
                }

                lock (_logBuffer)
                {
                    _logBuffer.Add(text);
                }

                _reportingTimer.Stop();
                _isReporting = false;
            }

            var timestampedMessage = $"{TimestampString}\t{message}";
            ListInsertHead(timestampedMessage);

            lock (_logBuffer)
            {
                _logBuffer.Add(timestampedMessage);
            }

            if (_logFile != null)
            {
                if (_logSemaphore.Wait(0))
                {
                    StartWriting();
                }
            }
        }

        private async void StartWriting()
        {
            var lines = new List<string>();

            bool spin;
            do
            {
                await Task.Delay(500);

                lock (_logBuffer)
                {
                    lines.AddRange(_logBuffer);
                    _logBuffer.Clear();
                }

                await FileIO.AppendLinesAsync(_logFile, lines);

                lock (_logBuffer)
                {
                    spin = _logBuffer.Count != 0;
                }
            }
            while (spin);

            _logSemaphore.Release();
        }

        private void Log(string message, GazeDevicePreview device)
        {
            var identifiedMessage = $"{message}\tId={device.Id}, State={device.ConfigurationState}, Eyes={device.CanTrackEyes}, Head={device.CanTrackHead}";
            Log(identifiedMessage);
        }

        private void Log(string message, GazePointPreview point)
        {
            var localTimestamp = TimeSpan.FromMilliseconds(point.Timestamp / 1000.0).ToString("G");

            var position = point.EyeGazePosition.HasValue ? point.EyeGazePosition.Value.ToString() : "No Position";

            Log($"{localTimestamp}\t{message}\t{position}");
        }

        private void OnDeviceAdded(GazeDeviceWatcherPreview sender, GazeDeviceWatcherAddedPreviewEventArgs args)
        {
            _deviceCount++;
            Log($"Device added, count={_deviceCount}", args.Device);

            if (!_isSourceHoooked)
            {
                _source.GazeEntered += OnGazeEntered;
                _source.GazeMoved += OnGazeMoved;
                _source.GazeExited += OnGazeExited;

                _isSourceHoooked = true;
            }
        }

        private void OnDeviceRemoved(GazeDeviceWatcherPreview sender, GazeDeviceWatcherRemovedPreviewEventArgs args)
        {
            _deviceCount--;
            Log($"Device removed, count={_deviceCount}", args.Device);
        }

        private void OnDeviceUpdated(GazeDeviceWatcherPreview sender, GazeDeviceWatcherUpdatedPreviewEventArgs args)
        {
            Log("Device updated", args.Device);
        }

        private void OnDeviceEnumerationCompleted(GazeDeviceWatcherPreview sender, object args)
        {
            Log($"Device enumeration complete, count={_deviceCount}");
        }

        private void OnGazeEntered(GazeInputSourcePreview sender, GazeEnteredPreviewEventArgs args)
        {
            _lastPosition = args.CurrentPoint.EyeGazePosition;
            Log(_isEntered ? "Enter while already entered" : "Gaze entered", args.CurrentPoint);

            _isEntered = true;
            _isFirstReportExpected = true;
        }

        private void OnGazeMoved(GazeInputSourcePreview sender, GazeMovedPreviewEventArgs args)
        {
            var points = args.GetIntermediatePoints();
            if (points.Count != 1)
            {
                Log($"Grouped points reported in move, Count={points.Count}");
            }

            foreach (var point in points)
            {
                var position = point.EyeGazePosition;
                var timestamp = TimeSpan.FromMilliseconds(point.Timestamp / 1000.0);

                if (!_isReporting)
                {
                    _startTimestamp = timestamp;
                    if (!_isEntered)
                    {
                        Log("Gaze report without enter");
                        _isEntered = true;
                    }
                    else if (_isFirstReportExpected && (_lastPosition.HasValue != position.HasValue ||
                        (_lastPosition.HasValue &&
                        !(_lastPosition.Value.X == position.Value.X &&
                        _lastPosition.Value.Y == position.Value.Y))))
                    {
                        Log("First report after enter is inconsistent", point);
                    }
                    _isFirstReportExpected = false;

                    _newPositionCount = position.HasValue ? 1 : 0;
                    _noPositionCount = position.HasValue ? 0 : 1;
                    _repeatPositionCount = 0;
                    _blickCount = 0;

                    _minDelta = TimeSpan.MaxValue;
                    _maxDelta = TimeSpan.MinValue;

                    _isReporting = true;

                    _reportingTimer.Start();
                }
                else
                {
                    var delta = timestamp - _lastTimestamp;
                    if (delta < _minDelta)
                    {
                        _minDelta = delta;
                    }
                    if (_maxDelta < delta)
                    {
                        _maxDelta = delta;
                    }

                    if (2*_minDelta<delta)
                    {
                        _blickCount++;
                    }

                    if (_lastPosition.HasValue)
                    {
                        if (position.HasValue)
                        {
                            if (_lastPosition == position)
                            {
                                _repeatPositionCount++;
                            }
                            else
                            {
                                _newPositionCount++;
                            }
                        }
                        else
                        {
                            _noPositionCount++;
                        }
                    }
                    else
                    {
                        if (position.HasValue)
                        {
                            _newPositionCount++;
                        }
                        else
                        {
                            _noPositionCount++;
                        }
                    }
                }
                _lastPosition = position;
                _lastTimestamp = timestamp;
                _lastTickCount = TimeSpan.FromMilliseconds(Environment.TickCount);
            }
        }

        private void OnGazeExited(GazeInputSourcePreview sender, GazeExitedPreviewEventArgs args)
        {
            Log(_isEntered ? "Gaze exited" : "Unexpected gaze exit without prior enter", args.CurrentPoint);

            _isEntered = false;
        }
    }
}
