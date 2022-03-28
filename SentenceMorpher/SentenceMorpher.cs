using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Morphology
{
    public class SentenceMorpher
    {
        /// <summary>
        /// Represents existing <see cref="string"/> roots with a <see cref="List{T}"/> of available <see cref="MorpherEntry"/> inflections
        /// </summary>
        private readonly Dictionary<string, List<MorpherEntry>> _internalDict;
        
        /// <summary>
        /// Constructor.
        /// </summary>
        private SentenceMorpher()
        {
            _internalDict = new Dictionary<string, List<MorpherEntry>>();
        }

        /// <summary>
        /// Adds new <see cref="MorpherEntry"/> entries for a given key if it exists, or creates a new key
        /// with the given <see cref="MorpherEntry"/> entries.
        /// </summary>
        /// <param name="key">Key (root) <see cref="string"/> word for the entries.</param>
        /// <param name="entries"><see cref="IEnumerable{T}"/> of <see cref="MorpherEntry"/> that should be added.</param>
        private void Put(string key, IEnumerable<MorpherEntry> entries)
        {
            if (ContainsKey(key))
                this[key].AddRange(entries);
            else
                _internalDict.Add(key, new List<MorpherEntry>(entries));
        }

        private List<MorpherEntry> this[string key] => _internalDict[key];

        private bool ContainsKey(string key) => _internalDict.ContainsKey(key);

        /// <summary>
        /// Searches for a suitable word in the internal dictionary of <see cref="SentenceMorpher"/> based on
        /// the given <see cref="MorpherEntry"/>. This method assumes that the given <see cref="MorpherEntry"/>
        /// has a root <see cref="MorpherEntry.Word"/> instead of the correct word - grams combination. If successful,
        /// returns a correct word for the given grams.
        /// </summary>
        /// <param name="entry"><see cref="MorpherEntry"/> that should be used for searching.</param>
        /// <returns>A <see cref="string"/> representing the correct word for the given grams.</returns>
        private string? FindWord(MorpherEntry entry)
        {
            // Currently entry.Word is incorrect. We should find the correct one
            if (!ContainsKey(entry.Word))
                return null;
            
            var entries = this[entry.Word];

            foreach (var x in entries)
            {
                if (entry.Grams.IsSubsetOf(x.Grams))
                    return x.Word;
            }

            return null;
        }
        
        /// <summary>
        ///     Создает <see cref="SentenceMorpher"/> из переданного набора строк словаря.
        /// </summary>
        /// <remarks>
        ///     В этом методе должен быть код инициализации: 
        ///     чтение и преобразование входных данных для дальнейшего их использования
        /// </remarks>
        /// <param name="dictionaryLines">
        ///     Строки исходного словаря OpenCorpora в формате plain-text.
        ///     <code> СЛОВО(знак_табуляции)ЧАСТЬ РЕЧИ( )атрибут1[, ]атрибут2[, ]атрибутN </code>
        /// </param>
        public static SentenceMorpher Create(IEnumerable<string> dictionaryLines)
        {
            var morpher = new SentenceMorpher();

            var isFillState = false; /* Splits the reading process into 2 parts: searching for a number describing 
                                        current word, and reading word states when it was found.
                                        */
            var currentEntries = new List<MorpherEntry>();

            foreach (var line in dictionaryLines)
            {
                if (isFillState)
                {
                    if (MorpherEntry.TryParse(line, out var resultEntry))
                    {
                        currentEntries.Add(resultEntry);
                    }
                    else
                    {
                        morpher.Put(currentEntries[0].Word, currentEntries);
                        currentEntries.Clear();
                        isFillState = false;
                    }
                }
                else
                {
                    if (!int.TryParse(line, out _)) continue;
                    
                    isFillState = true;
                }
            }

            if (isFillState) // Don't miss last entry
            {
                morpher.Put(currentEntries[0].Word, currentEntries);
                currentEntries.Clear();
            }

            return morpher;
        }

        /// <summary>
        ///     Выполняет склонение предложения согласно указанному формату
        /// </summary>
        /// <param name="sentence">
        ///     Входное предложение <para/>
        ///     Формат: набор слов, разделенных пробелами.
        ///     После слова может следовать спецификатор требуемой части речи (формат описан далее),
        ///     если он отсутствует - слово требуется перенести в выходное предложение без изменений.
        ///     Спецификатор имеет следующий формат: <code>{ЧАСТЬ РЕЧИ,аттрибут1,аттрибут2,..,аттрибутN}</code>
        ///     Если для спецификации найдётся несколько совпадений - используется первое из них
        /// </param>
        public virtual string Morph(string sentence)
        {
            var result = new StringBuilder();
            var split = sentence.Split(' ');

            foreach (var wordEntry in split)
            {
                if (wordEntry.Length == 0)
                    continue;

                if (!MorpherEntry.TryParse(wordEntry, out var entry))
                    throw new ArgumentException($"Invalid word entry (Received '{wordEntry}')");

                string? resultWord = null;

                if (entry.Grams.Count != 0)
                    resultWord = FindWord(entry);
                
                if (result.Length > 0)
                    result.Append(' ');
                result.Append(resultWord ?? entry.Word);
                
            }
            
            return result.ToString();
        }
    }

    /// <summary>
    /// Represents one morpher entry, in other words, a combination of word and grams.
    /// </summary>
    public readonly struct MorpherEntry
    {
        /// <summary>
        /// Word for this entry.
        /// </summary>
        public readonly string Word;
        /// <summary>
        /// <see cref="HashSet{T}"/> consisting of grams describing this word.
        /// </summary>
        public readonly HashSet<string> Grams;

        /// <summary>
        /// Constructor.
        /// </summary>
        public MorpherEntry(string word)
        {
            Word = word;
            Grams = new HashSet<string>();
        }

        /// <summary>
        /// Separators used to parse lines into <see cref="MorpherEntry"/>
        /// </summary>
        private static readonly char[] Separators = { ' ', ',', '\t', '{', '}' };
        
        /// <summary>
        /// Tries parsing a <see cref="string"/> into <see cref="MorpherEntry"/>. If successful,
        /// returns <see cref="result"/> as a correct <see cref="MorpherEntry"/>. If not, returns default <see cref="MorpherEntry"/>.
        /// </summary>
        /// <param name="line"><see cref="string"/> that should be parsed.</param>
        /// <param name="result">Parsed <see cref="MorpherEntry"/>.</param>
        /// <returns></returns>
        public static bool TryParse(string line, out MorpherEntry result)
        {
            var args = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            if (args.Length == 0)
            {
                result = default;
                return false;
            }
            
            result = new MorpherEntry(args[0].ToLower());

            for (var i = 1; i < args.Length; i++)
                result.Grams.Add(args[i].ToLower());

            return true;
        }
    }
}
