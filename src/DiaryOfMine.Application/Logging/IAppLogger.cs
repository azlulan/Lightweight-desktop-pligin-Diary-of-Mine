namespace DiaryOfMine.Application.Logging;

public interface IAppLogger
{
    ILogOperation BeginOperation(string eventName);
}

public interface ILogOperation : IDisposable
{
    void Complete(string result = "success");
    void Fail(Exception exception);
}
