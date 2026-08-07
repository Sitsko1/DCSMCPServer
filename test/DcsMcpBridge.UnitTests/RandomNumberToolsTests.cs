public class RandomNumberToolsTests
{
    [Fact]
    public void GetRandomNumber_DefaultRange_StaysWithinBounds()
    {
        var tools = new RandomNumberTools();

        for (int i = 0; i < 100; i++)
        {
            int value = tools.GetRandomNumber();
            Assert.InRange(value, 0, 99);
        }
    }

    [Fact]
    public void GetRandomNumber_CustomRange_StaysWithinBounds()
    {
        var tools = new RandomNumberTools();

        for (int i = 0; i < 100; i++)
        {
            int value = tools.GetRandomNumber(min: 10, max: 20);
            Assert.InRange(value, 10, 19);
        }
    }
}
