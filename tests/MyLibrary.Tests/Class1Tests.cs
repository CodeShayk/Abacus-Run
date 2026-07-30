using NUnit.Framework;

namespace MyLibrary.Tests;

public class Class1Tests
{
    [Test]
    public void Add_ReturnsSum()
    {
        var c = new MyLibrary.Class1();
        Assert.That(c.Add(2, 3), Is.EqualTo(5));
    }
}