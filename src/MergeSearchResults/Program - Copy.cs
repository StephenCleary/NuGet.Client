using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Newtonsoft.Json.Linq;
using NuGet.Indexing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MergeSearchResults
{
    class Program
    {
        static async Task<string> GetSearchResource(string source)
        {
            HttpClient client = new HttpClient();
            string result = await client.GetStringAsync(source);
            JObject index = JObject.Parse(result);

            string resourceId = "";
            foreach (var entry in index["resources"])
            {
                if (entry["@type"].ToString() == "SearchQueryService")
                {
                    resourceId = entry["@id"].ToString();
                    break;
                }
            }
            return resourceId;
        }

        static async Task<IDictionary<string, JObject>> GetSearchResults(string searchResource, string q)
        {
            string requestUri = string.Format("{0}?q={1}", searchResource, q);

            HttpClient client = new HttpClient();
            string s = await client.GetStringAsync(requestUri);
            JObject searchResults = JObject.Parse(s);

            int count = searchResults["data"].Count();

            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (JObject searchResult in searchResults["data"])
            {
                searchResult["rank"] = i++;
                searchResult["count"] = count;
                searchResult["resource"] = searchResource;
                result[searchResult["id"].ToString()] = searchResult;
            }
            return result;
        }

        static void MergeVersionLists(JObject lhs, JObject rhs)
        {
        }

        static IDictionary<string, JObject> Merge(IEnumerable<IDictionary<string, JObject>> results, string q)
        {
            //  (1) create a combined dictionary

            IDictionary<string, JObject> combined = null;
            bool rerank = false;
            foreach (var result in results)
            {
                if (combined == null)
                {
                    combined = new Dictionary<string, JObject>(result, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    foreach (var item in result)
                    {
                        JObject value;
                        if (combined.TryGetValue(item.Key, out value))
                        {
                            MergeVersionLists(value, item.Value);
                        }
                        else
                        {
                            rerank = true;
                            combined.Add(item);
                        }
                    }
                }
            }

            if (combined == null || !rerank)
            {
                return combined;
            }

            //  (2) create an in-memory Lucene index

            RAMDirectory directory = new RAMDirectory();
            using (IndexWriter writer = new IndexWriter(directory, new PackageAnalyzer(), IndexWriter.MaxFieldLength.UNLIMITED))
            {
                foreach (var item in combined.Values)
                {
                    writer.AddDocument(CreateDocument(item));
                }
                writer.Commit();
            }

            //  (3) re-query the in-memory index

            IndexSearcher searcher = new IndexSearcher(directory);

            Query query = NuGetQuery.MakeQuery(q);

            TopDocs topDocs = searcher.Search(query, 100);

            //  (4) make the combined results with the new ranking

            var combinedRanking = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int i = 0;
            foreach (ScoreDoc scoreDoc in topDocs.ScoreDocs)
            {
                string id = searcher.Doc(scoreDoc.Doc).Get("Id");
                combinedRanking[id] = i++;

                Console.WriteLine("RERANKED>>> {0}", id);
            }

            //  (5) now combine the results from the individual sources with a merged sort

            var combinedResult = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            // (5.1) create stacks - one for each source results

            var stacks = new List<Stack<JObject>>();

            foreach (var originalResult in results)
            {
                var ordered = new JObject[originalResult.Count];
                foreach (var obj in originalResult.Values)
                {
                    int rank = (int)obj["rank"];
                    ordered[rank] = obj;
                }

                var stack = new Stack<JObject>();
                foreach (var obj in ordered.Reverse())
                {
                    stack.Push(obj);
                }
                stacks.Add(stack);
            }

            int combinedRank = 0;
            string winner = null;
            do
            {
                winner = null;
                for (int j = 0; j < stacks.Count; j++)
                {
                    if (stacks[j].Count == 0)
                    {
                        continue;
                    }
                    var current = stacks[j].Peek()["id"].ToString();

                    if (winner == null)
                    {
                        winner = current;
                    }
                    else
                    {
                        if (combinedRanking[current] < combinedRanking[winner])
                        {
                            winner = current;
                        }
                    }
                }

                if (winner != null)
                {
                    foreach (var stack in stacks)
                    {
                        if (stack.Count == 0)
                        {
                            continue;
                        }
                        if (string.Equals(stack.Peek()["id"].ToString(), winner, StringComparison.OrdinalIgnoreCase))
                        {
                            stack.Pop();
                        }
                    }

                    if (!combinedResult.ContainsKey(winner))
                    {
                        combinedResult[winner] = combined[winner];
                        combinedResult[winner]["rank"] = combinedRank++;
                    }
                }
            }
            while (winner != null);

            return combinedResult;
        }

        static Document CreateDocument(JObject item)
        {
            Console.WriteLine("Indexing: {0}", item["id"]);

            Document doc = new Document();
            doc.Add(new Field("Id", item["id"].ToString(), Field.Store.YES, Field.Index.ANALYZED));
            doc.Add(new Field("Version", item["version"].ToString(), Field.Store.NO, Field.Index.ANALYZED));
            doc.Add(new Field("Summary", ((string)item["summary"]) ?? string.Empty, Field.Store.NO, Field.Index.ANALYZED));
            doc.Add(new Field("Description", ((string)item["description"]) ?? string.Empty, Field.Store.NO, Field.Index.ANALYZED));
            doc.Add(new Field("Title", ((string)item["title"]) ?? string.Empty, Field.Store.NO, Field.Index.ANALYZED));

            foreach (var tag in item["tags"] ?? Enumerable.Empty<JToken>())
            {
                doc.Add(new Field("Tags", tag.ToString(), Field.Store.NO, Field.Index.ANALYZED));
            }

            int rank = (int)item["rank"];
            int count = (int)item["count"];

            return doc;
        }

        static void Dump(IDictionary<string, JObject> results)
        {
            var ordered = new JObject[results.Count];
            foreach (var obj in results.Values)
            {
                int rank = (int)obj["rank"];
                ordered[rank] = obj;
            }

            foreach (var obj in ordered)
            {
                string rank = obj["rank"].ToString();
                string id = obj["id"].ToString();
                string resource = obj["resource"].ToString();
                string score = (string)obj["score"] ?? string.Empty;

                Console.Write(rank);
                for (int i = rank.Length; i < 10; i++)
                {
                    Console.Write(' ');
                }
                Console.Write(id);
                for (int i = id.Length; i < 52; i++)
                {
                    Console.Write(' ');
                }
                Console.Write(resource);
                for (int i = resource.Length; i < 56; i++)
                {
                    Console.Write(' ');
                }
                Console.WriteLine(score);
            }
        }

        static void Dump(IEnumerable<IDictionary<string, JObject>> results)
        {
            foreach (var result in results)
            {
                Dump(result);
                Console.WriteLine("--------------------");
            }
        }

        static async Task Test(string q)
        {
            var sources = new string[]
            {
                "http://api.nuget.org/v3/index.json",
                "http://api.dev.nugettest.org/v3-index/index.json"
            };

            var resources = new List<string>();
            foreach (var source in sources)
            {
                resources.Add(await GetSearchResource(source));
            }

            var tasks = new List<Task<IDictionary<string, JObject>>>();
            foreach (var resource in resources)
            {
                tasks.Add(GetSearchResults(resource, q));
            }

            await Task.WhenAll(tasks);

            Dump(tasks.Select(t => t.Result));

            Stopwatch sw = new Stopwatch();
            sw.Start();

            var merged = Merge(tasks.Select(t => t.Result), q);

            sw.Stop();
            Console.WriteLine($"Merged in: {sw.ElapsedMilliseconds}");
            Dump(merged);
        }

        static void Main(string[] args)
        {
            string q;

            while (true)
            {
                Console.Write("q = ");
                q = Console.ReadLine();

                if (string.IsNullOrEmpty(q))
                {
                    break;
                }

                Console.WriteLine(q);

                Test(q).Wait();
            }
        }
    }
}
