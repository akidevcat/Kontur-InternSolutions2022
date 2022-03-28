using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AutoComplete
{
    public struct FullName
    {
        public string Name;
        public string Surname;
        public string Patronymic;
    }

    public class AutoCompleter
    {
        /// <summary>
        /// Represents a DS to store parsed to string full names for fast search operations.
        /// </summary>
        private readonly AlphabetTrie _fullNamesAlphabetTrie;

        /// <summary>
        /// Constructor.
        /// </summary>
        public AutoCompleter()
        {
            _fullNamesAlphabetTrie = new AlphabetTrie();
        }
        
        /// <summary>
        /// Adds a list of <see cref="FullName"/> items to this <see cref="AutoCompleter"/>.
        /// </summary>
        /// <param name="fullNames">A list of <see cref="FullName"/> items to add.</param>
        public void AddToSearch(List<FullName> fullNames)
        {
            foreach (var fullName in fullNames)
                _fullNamesAlphabetTrie.InsertWord(ConvertFullNameToString(fullName));
        }

        /// <summary>
        /// Searches for suitable names that start with the given prefix.
        /// </summary>
        /// <param name="prefix">Prefix specifying the result names. All returned names will be starting with this
        /// prefix.</param>
        /// <returns>Found names.</returns>
        /// <exception cref="ArgumentException">Thrown if length of the given prefix is not suitable (equal to zero or
        /// greater than limit).</exception>
        public List<string> Search(string prefix)
        {
            const int prefixMaxLen = 100;

            var normalizedPrefix = NormalizeSpaces(prefix);
            
            if (normalizedPrefix.Length > 100)
                throw new ArgumentException($"{nameof(prefix)} length cannot be more than {prefixMaxLen}");
            if (normalizedPrefix.Length == 0)
                throw new ArgumentException($"{nameof(normalizedPrefix)} length cannot be equal to zero");

            return _fullNamesAlphabetTrie.FindWordsByPrefix(normalizedPrefix).Select(CapitalizeWords).ToList();
        }

        /// <summary>
        /// Converts <see cref="FullName"/> to a proper string in a format of "$Surname $Name $Patronymic" with exactly
        /// one space between each of them.
        /// </summary>
        /// <param name="fullName"><see cref="FullName"/> that should be converted.</param>
        /// <returns>String in a format of "$Surname $Name $Patronymic"</returns>
        private static string ConvertFullNameToString(FullName fullName)
        {
            var name = fullName.Name;
            var surname = fullName.Surname;
            var patronymic = fullName.Patronymic;
            
            if (name != null)
                name = name.Trim();
            if (surname != null)
                surname = surname.Trim();
            if (patronymic != null)
                patronymic = patronymic.Trim();

            return JoinFullNameParts(surname, name, patronymic);
        }

        /// <summary>
        /// Joins several <see cref="values"/> into a result string of format "$value0 $value1... $valueN" skipping
        /// empty or null strings.
        /// </summary>
        /// <param name="values">Substrings that should be joined together. These substrings can be null or empty.</param>
        /// <returns>string of format "$value0 $value1... $valueN".</returns>
        private static string JoinFullNameParts(params string[] values)
        {
            var builder = new StringBuilder();
            
            foreach (var value in values)
            {
                if (value == null)
                    continue;
                if (value.Length == 0)
                    continue;
                
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(value);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Removes all sequential occurrences of spaces (' ') with a single space and trims the string.
        /// </summary>
        /// <param name="value"><see cref="string"/> that should be normalized.</param>
        /// <returns>A normalized <see cref="string"/>.</returns>
        private static string NormalizeSpaces(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        /// <summary>
        /// Capitalizes all words in the given sentence.
        /// </summary>
        /// <param name="sentence">Sentence where words should be capitalized.</param>
        /// <returns>A sentence with all words capitalized.</returns>
        private static string CapitalizeWords(string sentence) => string.Join(' ', 
            sentence.Split(' ').Select(x => x[0].ToString().ToUpper() + x.Substring(1)));
    }

    public static class TrieMappers
    {
        private const int ENGLISH_IGNORE_UPPERCASE_SIZE = 'z' - 'a' + 1;
        private const int RUSSIAN_IGNORE_UPPERCASE_SIZE = 'я' - 'а' + 1;
        public const int RUSSIAN_ENGLISH_IGNORE_UPPERCASE_SIZE = ENGLISH_IGNORE_UPPERCASE_SIZE + 
                                                                 RUSSIAN_IGNORE_UPPERCASE_SIZE + 1; // One is saved for spaces

        /// <summary>
        /// Maps a character from Latin or Cyrillic alphabets to a simpler byte form saving up memory.
        /// Characters are distributed in the following order: 0 (' ' character), 1 -> 26 (Latin alphabet), 27 -> 59 (Cyrillic alphabet)
        /// </summary>
        /// <param name="c">Character that should be mapped.</param>
        /// <returns>Corresponding byte value.</returns>
        /// <exception cref="ArgumentException">Thrown if the given character is not supported.</exception>
        public static byte MapFromRussianEnglishIgnoreUppercase(char c)
        {
            return c switch
            {
                ' ' => 0,
                >= 'A' and <= 'Z' => (byte) (c - 'A' + 1),
                >= 'a' and <= 'z' => (byte) (c - 'a' + 1),
                >= 'А' and <= 'Я' => (byte) (c - 'А' + ENGLISH_IGNORE_UPPERCASE_SIZE + 1),
                >= 'а' and <= 'я' => (byte) (c - 'а' + ENGLISH_IGNORE_UPPERCASE_SIZE + 1),
                _ => throw new ArgumentException($"Mapping from char '{c}' to {typeof(byte)} is not supported")
            };
        }
    }
    
    /// <summary>
    /// A custom trie implementation for Latin and Cyrillic alphabets.
    /// </summary>
    public class AlphabetTrie
    {
        private readonly int _alphabetSize;
        private readonly TrieNode _initialNode;

        /// <summary>
        /// Constructor.
        /// </summary>
        public AlphabetTrie()
        {
            _alphabetSize = TrieMappers.RUSSIAN_ENGLISH_IGNORE_UPPERCASE_SIZE;
            _initialNode = new TrieNode(_alphabetSize);
        }

        /// <summary>
        /// Inserts a new word into the trie.
        /// </summary>
        /// <param name="word">Word that should be inserted.</param>
        public void InsertWord(string word)
        {
            var currentNode = _initialNode;
            foreach (var c in word)
            {
                var cIndex = TrieMappers.MapFromRussianEnglishIgnoreUppercase(c);
                if (!currentNode.Contains(cIndex))
                    currentNode[cIndex] = new TrieNode(_alphabetSize);
                currentNode = currentNode[cIndex];
            }

            currentNode.Value = word;
            currentNode.Final = true;
        }

        /// <summary>
        /// Finds all children words by the given prefix including prefix if it is an existing word.
        /// </summary>
        /// <param name="prefix">Prefix that defines which words should be returned.
        /// All found words start with this prefix.</param>
        /// <returns><see cref="IEnumerable{T}"/> representing all found words.</returns>
        public IEnumerable<string> FindWordsByPrefix(string prefix)
        {
            var result = new List<string>();

            var currentNode = _initialNode;
            
            foreach (var c in prefix)
            {
                var cIndex = TrieMappers.MapFromRussianEnglishIgnoreUppercase(c);
                currentNode = currentNode[cIndex];
                if (currentNode == null)
                    return result;
            }

            FindChildrenWords(result, currentNode);

            return result;
        }

        /// <summary>
        /// Internal recursive method to search for underlying words by the divide-and-conquer principle. 
        /// </summary>
        /// <param name="addTo"><see cref="ICollection{T}"/> that should be filled with results.</param>
        /// <param name="node">Current recursion node.</param>
        private static void FindChildrenWords(ICollection<string> addTo, TrieNode node)
        {
            if (node.Final)
                addTo.Add(node.Value);
            
            for (var cIndex = (byte)0; cIndex < node.Children.Length; cIndex++)
            {
                var n = node.Children[cIndex];
                if (n == null) continue;

                FindChildrenWords(addTo, n);
            }
        }
    }
    
    /// <summary>
    /// <see cref="AlphabetTrie"/>'s node.
    /// </summary>
    public class TrieNode
    {
        /// <summary>
        /// Is this node final? (Represents a word)
        /// </summary>
        public bool Final { get; set; }
        /// <summary>
        /// Stored value for this node to speed up the search process.
        /// </summary>
        public string Value { get; set; }
        /// <summary>
        /// Underlying children <see cref="TrieNode"/>s.
        /// </summary>
        public readonly TrieNode[] Children;
        
        /// <summary>
        /// Constructor.
        /// </summary>
        public TrieNode(int alphabetSize, string value = null)
        {
            Children = new TrieNode[alphabetSize];
            Value = value;
            Final = false;
        }

        /// <summary>
        /// Checks if this <see cref="TrieNode"/> contains a child <see cref="TrieNode"/> with the given index.
        /// </summary>
        /// <param name="index">Index of the child <see cref="TrieNode"/>.</param>
        /// <returns>True if this <see cref="TrieNode"/> contains the specified <see cref="TrieNode"/>.</returns>
        public bool Contains(int index) => this[index] != null;
                
        public TrieNode this[int index]
        {
            get => Children[index];
            set => Children[index] = value;
        }
    }
}