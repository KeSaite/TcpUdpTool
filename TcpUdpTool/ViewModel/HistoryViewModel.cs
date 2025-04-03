using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Threading.Tasks;
using TcpUdpTool.Model;
using TcpUdpTool.Model.Data;
using TcpUdpTool.Model.Formatter;
using TcpUdpTool.Model.Util;
using TcpUdpTool.ViewModel.Helper;
using TcpUdpTool.ViewModel.Item;
using TcpUdpTool.ViewModel.Base;
using System.Linq;

namespace TcpUdpTool.ViewModel
{
    public class HistoryViewModel : ObservableObject, IContentChangedHelper, IDisposable
    {
        #region private members

        private BlockingCollection<Transmission> _incomingQueue;
        private RateMonitor _rateMonitor;
        private IFormatter _formatter;
        private DispatcherTimer _updateTimer;
        private long _lastPackageDiscarded;
        private readonly object _updateLock = new object();
        private DateTime _lastUpdateTime = DateTime.Now;

        #endregion

        #region public properties

        private string _header;
        public string Header
        {
            get { return _header; }
            set
            {
                _header = value;
                OnPropertyChanged(nameof(Header));
            }
        }

        private BatchObservableCollection<ConversationItemViewModel> _conversation;
        public BatchObservableCollection<ConversationItemViewModel> Conversation
        {
            get { return _conversation; }
        }

        private string _totalReceived;
        public string TotalReceived
        {
            get { return _totalReceived; }
            set
            {
                if (_totalReceived != value)
                {
                    _totalReceived = value;
                    OnPropertyChanged(nameof(TotalReceived));
                }
            }
        }

        private string _rateReceive;
        public string RateReceive
        {
            get { return _rateReceive; }
            set
            {
                if (_rateReceive != value)
                {
                    _rateReceive = value;
                    OnPropertyChanged(nameof(RateReceive));
                }
            }
        }

        private string _totalSent;
        public string TotalSent
        {
            get { return _totalSent; }
            set
            {
                if (_totalSent != value)
                {
                    _totalSent = value;
                    OnPropertyChanged(nameof(TotalSent));
                }
            }
        }

        private string _rateSend;
        public string RateSend
        {
            get { return _rateSend; }
            set
            {
                if (_rateSend != value)
                {
                    _rateSend = value;
                    OnPropertyChanged(nameof(RateSend));
                }
            }
        }

        private bool _plainTextSelected;
        public bool PlainTextSelected
        {
            get { return _plainTextSelected; }
            set
            {
                if (value != _plainTextSelected)
                {
                    _plainTextSelected = value;
                    OnPropertyChanged(nameof(PlainTextSelected));
                }
            }
        }

        private bool _hexSelected;
        public bool HexSelected
        {
            get { return _hexSelected; }
            set
            {
                if (value != _hexSelected)
                {
                    _hexSelected = value;
                    OnPropertyChanged(nameof(HexSelected));
                }
            }
        }

        private bool _statisticsSelected;
        public bool StatisticsSelected
        {
            get { return _statisticsSelected; }
            set
            {
                if (value != _statisticsSelected)
                {
                    _statisticsSelected = value;
                    OnPropertyChanged(nameof(StatisticsSelected));
                }
            }
        }

        private bool _packageDiscardedWarning;
        public bool PackageDiscardedWarning
        {
            get { return _packageDiscardedWarning; }
            set
            {
                if (_packageDiscardedWarning != value)
                {
                    _packageDiscardedWarning = value;
                    OnPropertyChanged(nameof(PackageDiscardedWarning));
                }
            }
        }

        private bool _isProcessingLargeData;
        public bool IsProcessingLargeData
        {
            get { return _isProcessingLargeData; }
            set
            {
                if (_isProcessingLargeData != value)
                {
                    _isProcessingLargeData = value;
                    OnPropertyChanged(nameof(IsProcessingLargeData));
                }
            }
        }

        private string _searchText;
        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged(nameof(SearchText));
                    ApplySearch();
                }
            }
        }

        #endregion

        #region public commands

        public ICommand ClearCommand
        {
            get { return new DelegateCommand(Clear); }
        }

        public ICommand ViewChangedCommand
        {
            get { return new DelegateCommand(ViewChanged); }
        }

        public ICommand CopyCommand
        {
            get { return new DelegateCommand(Copy); }
        }

        public ICommand SaveCommand
        {
            get { return new DelegateCommand(Save); }
        }

        public ICommand SearchCommand
        {
            get { return new DelegateCommand(ApplySearch); }
        }

        #endregion

        #region public events

        public event Action ContentChanged;

        #endregion

        #region constructors

        private int _userHistoryCount;

        public HistoryViewModel()
        {
            _rateMonitor = new RateMonitor();
            _incomingQueue = new BlockingCollection<Transmission>(100);
            _conversation = new BatchObservableCollection<ConversationItemViewModel>();
            _conversation.CollectionChanged += (sender, e) => ContentChanged?.Invoke();
            _formatter = new PlainTextFormatter();
            _updateTimer = new DispatcherTimer();
            _updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 50);
            _updateTimer.Tick += (sender, arg) => OnUpdateUI();

            _userHistoryCount = Properties.Settings.Default.HistoryEntries;

            PlainTextSelected = true;

            _rateMonitor.Start();
            _updateTimer.Start();


            Properties.Settings.Default.PropertyChanged +=
                (sender, e) =>
                {
                    if (e.PropertyName == nameof(Properties.Settings.Default.HistoryEntries))
                    {
                        _userHistoryCount = Properties.Settings.Default.HistoryEntries;

                        int queueCapacity = Math.Max(100, _userHistoryCount);
                        _incomingQueue = new BlockingCollection<Transmission>(queueCapacity);
                    }
                    else if (e.PropertyName == nameof(Properties.Settings.Default.Encoding))
                    {
                        ViewChanged();
                    }
                    else if (e.PropertyName == nameof(Properties.Settings.Default.HistoryInfoTimestamp) ||
                             e.PropertyName == nameof(Properties.Settings.Default.HistoryInfoIpAdress))
                    {
                        ViewChanged();
                    }
                };
        }

        #endregion

        #region public functions

        public void Append(Transmission msg)
        {
            if (msg.IsReceived)
            {
                _rateMonitor.NoteReceived(msg);
            }
            else
            {
                _rateMonitor.NoteSent(msg);
            }

            if (!_incomingQueue.TryAdd(msg))
            {
                _lastPackageDiscarded = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            }

            if (_incomingQueue.Count > _incomingQueue.BoundedCapacity * 0.8)
            {
                TriggerPrioritizedUpdate();
            }
        }

        private void Clear()
        {
            Conversation.Clear();

            if (StatisticsSelected)
            {
                _rateMonitor.Reset();
                OnUpdateUI();
            }
        }

        private void ViewChanged()
        {
            if (PlainTextSelected)
            {
                _formatter = new PlainTextFormatter(SettingsUtils.GetEncoding());
                ApplyFormatter(_formatter);
            }
            else if (HexSelected)
            {
                _formatter = new HexFormatter();
                ApplyFormatter(_formatter);
            }
        }

        #endregion

        #region private functions

        private void TriggerPrioritizedUpdate()
        {
            lock (_updateLock)
            {
                TimeSpan elapsed = DateTime.Now - _lastUpdateTime;
                if (elapsed.TotalMilliseconds > 250)
                {
                    DispatchHelper.Invoke(() =>
                    {
                        OnUpdateUI();
                        _lastUpdateTime = DateTime.Now;
                    });
                }
            }
        }

        private void ApplySearch()
        {
            if (string.IsNullOrEmpty(SearchText))
            {
                foreach (var item in _conversation)
                {
                    item.IsVisible = true;
                }
            }
            else
            {
                string searchUpper = SearchText.ToUpperInvariant();

                foreach (var item in _conversation)
                {
                    string contentUpper = item.Content.ToUpperInvariant();
                    item.IsVisible = contentUpper.Contains(searchUpper);
                }
            }
        }

        private void UpdateStatistics()
        {
            TotalReceived = StringFormatUtils.GetSizeAsString(_rateMonitor.TotalReceivedBytes);
            RateReceive = StringFormatUtils.GetRateAsString(_rateMonitor.CurrentReceiveRate) +
                " (" + StringFormatUtils.GetSizeAsString(_rateMonitor.CurrentReceiveRate / 8) + "/s)";
            TotalSent = StringFormatUtils.GetSizeAsString(_rateMonitor.TotalSentBytes);
            RateSend = StringFormatUtils.GetRateAsString(_rateMonitor.CurrentSendRate) +
                " (" + StringFormatUtils.GetSizeAsString(_rateMonitor.CurrentSendRate / 8) + "/s)";
        }

        private void AdjustUpdateFrequency()
        {
            if (_rateMonitor.CurrentReceiveRate > 500000)
            {
                _updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 200);
            }
            else if (_rateMonitor.CurrentReceiveRate > 100000)
            {
                _updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 100);
            }
            else if (_incomingQueue.Count > 0)
            {
                _updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 50);
            }
            else
            {
                _updateTimer.Interval = new TimeSpan(0, 0, 0, 0, 200);
            }
        }

        private void Copy()
        {
            Clipboard.SetText(GetSelectedItemsAsString());
        }

        private void Save()
        {
            string data = GetSelectedItemsAsString();

            var dialog = new SaveFileDialog();
            dialog.Filter = "Text file (*.txt)|*.txt";

            if (dialog.ShowDialog().GetValueOrDefault())
            {
                try
                {
                    File.WriteAllText(dialog.FileName, data);
                }
                catch (Exception ex)
                {
                    DialogUtils.ShowErrorDialog("Failed to save file. " + ex.Message);
                }
            }
        }

        private string GetSelectedItemsAsString()
        {
            var sb = new StringBuilder();
            foreach (var item in _conversation)
            {
                if (item.IsSelected)
                {
                    if (item.TimestampVisible)
                    {
                        sb.Append(item.Timestamp);
                    }

                    if (item.SourceVisible)
                    {
                        sb.Append(item.Source);
                    }

                    sb.Append(item.IsReceived ? "R:" : "S:");
                    sb.AppendLine();
                    sb.AppendLine(item.Content);
                }
            }

            return sb.ToString();
        }

        private void ApplyFormatter(IFormatter formatter)
        {
            foreach (var item in _conversation)
            {
                item.SetFormatter(formatter);
            }
        }

        private void OnUpdateUI()
        {
            lock (_updateLock)
            {
                IsProcessingLargeData = _incomingQueue.Count > 50;

                if (StatisticsSelected)
                {
                    PackageDiscardedWarning = false;
                    UpdateStatistics();

                    if (_rateMonitor.CurrentReceiveRate > 100000)
                    {
                        return;
                    }
                }
                else
                {
                    PackageDiscardedWarning = ((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - _lastPackageDiscarded) < 10000;
                }

                UpdateMemoryUsage();

                int batchSize = DetermineBatchSize();
                if (batchSize > 0)
                {
                    UpdateConversationView(batchSize);
                }

                _lastUpdateTime = DateTime.Now;

                AdjustUpdateFrequency();
            }

        }

        private int DetermineBatchSize()
        {
            int queueSize = _incomingQueue.Count;

            if (queueSize < 10) return queueSize;

            if (queueSize < 100) return queueSize / 2;

            return 50;
        }

        private void UpdateConversationView(int batchSize)
        {
            _conversation.BeginBatch();

            Transmission msg;
            int count = 0;

            while (count < batchSize && _incomingQueue.TryTake(out msg))
            {
                _conversation.Add(new ConversationItemViewModel(msg, _formatter));
                count++;
            }

            while (_conversation.Count > _userHistoryCount)
            {
                _conversation.RemoveAt(0);
            }

            _conversation.EndBatch();
        }

        private string _memoryUsage;
        public string MemoryUsage
        {
            get { return _memoryUsage; }
            set
            {
                if (_memoryUsage != value)
                {
                    _memoryUsage = value;
                    OnPropertyChanged(nameof(MemoryUsage));
                }
            }
        }
        private void UpdateMemoryUsage()
        {
            if (StatisticsSelected)
            {
                long memBytes = GC.GetTotalMemory(false);
                MemoryUsage = StringFormatUtils.GetSizeAsString((ulong)memBytes);
            }
        }
        public void Dispose()
        {
            _incomingQueue?.Dispose();
            _rateMonitor?.Dispose();
            _updateTimer?.Stop();
        }

        #endregion
    }
}