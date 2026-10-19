using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class LayoutReadResultTests
{
    [Test]
    public void Success_PreservesValueAndSupportsDeconstruction()
    {
        var (status, value, requestedByteCount) = LayoutReadResult<int>.Success(42);

        Assert.That(status, Is.EqualTo(ReadResult.Success));
        Assert.That(value, Is.EqualTo(42));
        Assert.That(requestedByteCount, Is.Null);
    }

    [Test]
    public void Success_CompletesAHookWithoutAValue()
    {
        var (status, requestedByteCount) = LayoutReadResult.Success();

        Assert.That(status, Is.EqualTo(ReadResult.Success));
        Assert.That(requestedByteCount, Is.Null);
    }

    [TestCase(null)]
    [TestCase(1)]
    [TestCase(20)]
    public void NeedMoreData_PreservesOptionalMinimumTotalInput(int? minimum)
    {
        var result = LayoutReadResult<EncodedField>.NeedMoreData(minimum);
        var operation = LayoutReadResult.NeedMoreData(minimum);

        Assert.That(result.Status, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(result.Value, Is.Null);
        Assert.That(result.RequestedByteCount, Is.EqualTo(minimum));
        Assert.That(operation.Status, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(operation.RequestedByteCount, Is.EqualTo(minimum));
    }

    [TestCase(ReadResult.Success)]
    [TestCase(ReadResult.NeedMoreData)]
    [TestCase(ReadResult.EndOfData)]
    [TestCase(ReadResult.Failure)]
    public void WithStatus_PreservesStatusWithoutAValueOrByteCount(ReadResult status)
    {
        var result = LayoutReadResult<EncodedField>.WithStatus(status);
        var operation = LayoutReadResult.WithStatus(status);

        Assert.That(result.Status, Is.EqualTo(status));
        Assert.That(result.Value, Is.Null);
        Assert.That(result.RequestedByteCount, Is.Null);
        Assert.That(operation.Status, Is.EqualTo(status));
        Assert.That(operation.RequestedByteCount, Is.Null);
    }

    [Test]
    public void FailureAndEndOfData_HaveNoValueOrByteCount()
    {
        var failure = LayoutReadResult<EncodedField>.Failure();
        var end = LayoutReadResult<EncodedField>.EndOfData();

        Assert.That(failure, Is.EqualTo(new LayoutReadResult<EncodedField>(ReadResult.Failure, null!)));
        Assert.That(end, Is.EqualTo(new LayoutReadResult<EncodedField>(ReadResult.EndOfData, null!)));
        Assert.That(LayoutReadResult.Failure(), Is.EqualTo(new LayoutReadResult(ReadResult.Failure)));
        Assert.That(LayoutReadResult.EndOfData(), Is.EqualTo(new LayoutReadResult(ReadResult.EndOfData)));
    }
}
