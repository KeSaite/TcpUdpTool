using System;
using System.Timers;

namespace TcpUdpTool.Model
{
    public class CyclicSender : IDisposable
    {
        private Timer _timer;
        private Action _sendAction;
        private bool _isRunning;

        public int Interval { get; private set; }

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public CyclicSender()
        {
            _timer = new Timer();
            _timer.AutoReset = true;
            _timer.Elapsed += OnTimerElapsed;
            Interval = 1000; // Default to 1 second
        }

        public void Start(Action sendAction, int intervalMs)
        {
            if (_isRunning)
                return;

            if (intervalMs < 1)
                intervalMs = 1; // Ensure minimum 1ms interval

            _sendAction = sendAction;
            Interval = intervalMs;
            _timer.Interval = intervalMs;
            _timer.Start();
            _isRunning = true;
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            _timer.Stop();
            _isRunning = false;
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                _sendAction?.Invoke();
            }
            catch (Exception)
            {
                // Ignore exceptions in cyclic sender to keep the timer running
                // This prevents one failed send from stopping the cycle
            }
        }

        public void Dispose()
        {
            Stop();
            _timer?.Dispose();
            _timer = null;
        }
    }
}