namespace FanucSimulator
{
    public class Alarm
    {
        public int Number { get; }
        public string Message { get; }

        public Alarm(int number, string message)
        {
            Number = number;
            Message = message;
        }

        public override string ToString() => $"ALARM {Number}: {Message}";
    }
}
