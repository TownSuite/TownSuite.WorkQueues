namespace TownSuite.WorkQueues;

public class WorkQueuesException : Exception
{
    public WorkQueuesException(string message) : base(message)
    {
    }

    public WorkQueuesException(string message, Exception ex) : base(message, ex)
    {
    }
}
