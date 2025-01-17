using NUnit.Framework;
using Land.Markup.Binding;
using System.Runtime.Remoting.Messaging;

namespace Land.Markup.Tests
{
    [TestFixture]
    public class FuzzyHashingTests
    {
       /* [Test]
        public void CompareTexts_IdenticalVariedTexts_Returns1()
        {
            // Arrange
            string text1 = "The quick brown fox jumps over the lazy dog 1234567890"
                         + "Lorem ipsum dolor sit amet, consectetur adipiscing elit."
                         + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
                         + "!@#$%^&*()_+<>?/|{}[]`~";
            string text2 = text1;

            // Act
            double similarity = FuzzyHashing.CompareTexts(text1, text2);

            // Assert
            Assert.AreEqual(1.0, similarity, 0.0001);
        }*/

        [Test]
        public void CompareTexts_CompletelyDifferentVariedTexts_Returns0()
        {
            // Arrange
            string text1 = "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife. However little known the feelings or views of such a man may be on his first entering a neighbourhood, this truth is so well fixed in the minds of the surrounding families, that he is considered the rightful property of some one or other of their daughters. 'My dear Mr. Bennet,' said his lady to him one day, 'have you heard that Netherfield Park is let at last?'";

            string text2 = "It was the best of times, it was the worst of times, it was the age of wisdom, it was the age of foolishness, it was the epoch of belief, it was the epoch of incredulity, it was the season of Light, it was the season of Darkness, it was the spring of hope, it was the winter of despair, we had everything before us, we had nothing before us, we were all going direct to Heaven, we were all going direct the other way—in short, the period was so far like the present period.";

            // Act
            double similarity = FuzzyHashing.CompareTexts(text1, text2);

            // Assert
            Assert.AreEqual(0.0, similarity, 0.0001);
        }

        [Test]
        public void CompareTexts_SlightlyDifferentVariedTexts_ReturnsHighSimilarity()
        {
            // Arrange
            string text1 = "The quick brown fox jumps over the lazy dog 1234567890"
                         + "Lorem ipsum dolor sit amet, consectetur adipiscing elit."
                         + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
                         + "!@#$%^&*()_+<>?/|{}[]`~";

            string text2 = text1 + " Just a bit of extra data at the end.";

            // Act
            double similarity = FuzzyHashing.CompareTexts(text1, text2);

            // Assert
            Assert.GreaterOrEqual(similarity, 0.8);
        }
    }
}
