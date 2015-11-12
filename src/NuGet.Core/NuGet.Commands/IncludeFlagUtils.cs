﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NuGet.DependencyResolver;
using NuGet.LibraryModel;
using NuGet.ProjectModel;

namespace NuGet.Commands
{
    internal static class IncludeFlagUtils
    {
        internal static Dictionary<string, LibraryIncludeType> FlattenDependencyTypes(
            RestoreTargetGraph targetGraph,
            PackageSpec spec)
        {
            var result = new Dictionary<string, LibraryIncludeType>(StringComparer.OrdinalIgnoreCase);

            FlattenDependencyTypesUnified3(result, targetGraph);

            // Override flags for direct dependencies
            var directDependencies = spec.Dependencies.ToList();

            // Add dependencies defined under the framework node
            var specFramework = spec.GetTargetFramework(targetGraph.Framework);
            if (specFramework != null && specFramework.Dependencies != null)
            {
                directDependencies.AddRange(specFramework.Dependencies);
            }

            foreach (var dependency in directDependencies)
            {
                if (result.ContainsKey(dependency.Name))
                {
                    result[dependency.Name] = dependency.IncludeType;
                }
                else
                {
                    result.Add(dependency.Name, dependency.IncludeType);
                }
            }

            return result;
        }

        private static void FlattenDependencyTypesUnified3(
            Dictionary<string, LibraryIncludeType> result,
            RestoreTargetGraph targetGraph)
        {
            var nodeQueue = new Queue<Node>(1);
            Node node = null;

            var unifiedNodes = new Dictionary<string, GraphItem<RemoteResolveResult>>(StringComparer.OrdinalIgnoreCase);

            // Create a look up table of id -> library
            // This should contain only packages and projects. If there is a project with the 
            // same name as a package, use the project.
            // Projects take precedence over packages here to match the resolver behavior.
            foreach (var item in targetGraph.Flattened
                .OrderBy(lib => OrderType(lib)))
            {
                // Include flags only apply to packages and projects
                if (IsPackageOrProject(item) && !unifiedNodes.ContainsKey(item.Key.Name))
                {
                    unifiedNodes.Add(item.Key.Name, item);
                }
            }

            // Walk all graphs and merge the results
            foreach (var graph in targetGraph.Graphs)
            {
                // The top level edge contains only the root node.
                var outerEdge = new GraphEdge<RemoteResolveResult>(outerEdge: null, item: graph.Item, edge: null);

                foreach (var root in graph.InnerNodes)
                {
                    // Walk only the projects and packages
                    GraphItem<RemoteResolveResult> unifiedRoot;
                    if (unifiedNodes.TryGetValue(root.Key.Name, out unifiedRoot))
                    {
                        // Find the initial project -> dependency flags
                        var typeIntersection = GetDependencyType(graph, root);

                        node = new Node()
                        {
                            OuterEdge = outerEdge,
                            DependencyType = typeIntersection,
                            Root = root.Item
                        };

                        nodeQueue.Enqueue(node);
                    }
                }
            }

            while (nodeQueue.Count > 0)
            {
                node = nodeQueue.Dequeue();
                var rootId = node.Root.Key.Name;

                // Combine results on the way up
                LibraryIncludeType currentTypes;
                if (result.TryGetValue(rootId, out currentTypes))
                {
                    if (ReferenceEquals(currentTypes, node.DependencyType)
                        || (
                            node.DependencyType.Keywords.Count <= currentTypes.Keywords.Count
                            && node.DependencyType.Keywords.IsSubsetOf(currentTypes.Keywords))
                        )
                    {
                        // Noop, this is done
                        continue;
                    }

                    result[rootId] = currentTypes.Combine(node.DependencyType);
                }
                else
                {
                    result.Add(rootId, node.DependencyType);
                }

                foreach (var dependency in node.Root.Data.Dependencies)
                {
                    // Any nodes that are not in unifiedNodes are types that should be ignored
                    // We should also ignore dependencies that are excluded
                    GraphItem<RemoteResolveResult> child;
                    if (unifiedNodes.TryGetValue(dependency.Name, out child)
                        && !dependency.SuppressParent.Equals(LibraryIncludeType.All))
                    {
                        var typeIntersection = node.DependencyType.Intersect(dependency.IncludeType)
                            .Except(dependency.SuppressParent);

                        var innerEdge = new GraphEdge<RemoteResolveResult>(node.OuterEdge, node.Root, dependency);

                        var childNode = new Node()
                        {
                            OuterEdge = innerEdge,
                            DependencyType = typeIntersection,
                            Root = child
                        };

                        nodeQueue.Enqueue(childNode);
                    }
                }
            }
        }

        private static void FlattenDependencyTypesUnified2(
            Dictionary<string, LibraryIncludeType> result,
            GraphItem<RemoteResolveResult> root,
            GraphEdge<RemoteResolveResult> outerEdge,
            Dictionary<string, GraphItem<RemoteResolveResult>> unifiedNodes,
            LibraryIncludeType dependencyType)
        {
            var nodeQueue = new Queue<Node>();

            var node = new Node()
            {
                OuterEdge = outerEdge,
                DependencyType = dependencyType,
                Root = root
            };

            while (node != null)
            {
                var rootId = node.Root.Key.Name;

                // Combine results on the way up
                LibraryIncludeType currentTypes;
                if (result.TryGetValue(rootId, out currentTypes))
                {
                    if (ReferenceEquals(currentTypes, node.DependencyType)
                        || (
                            node.DependencyType.Keywords.Count <= currentTypes.Keywords.Count
                            && node.DependencyType.Keywords.IsSubsetOf(currentTypes.Keywords))
                        )
                    {
                        // Noop, this is done
                        node = null;
                        if (nodeQueue.Count > 0)
                        {
                            node = nodeQueue.Dequeue();
                        }
                        continue;
                    }

                    result[rootId] = currentTypes.Combine(node.DependencyType);
                }
                else
                {
                    result.Add(rootId, node.DependencyType);
                }

                foreach (var dependency in node.Root.Data.Dependencies)
                {
                    // Any nodes that are not in unifiedNodes are types that should be ignored
                    // We should also ignore dependencies that are excluded
                    GraphItem<RemoteResolveResult> child;
                    if (unifiedNodes.TryGetValue(dependency.Name, out child)
                        && !dependency.SuppressParent.Equals(LibraryIncludeType.All))
                    {
                        var typeIntersection = node.DependencyType.Intersect(dependency.IncludeType)
                            .Except(dependency.SuppressParent);

                        var innerEdge = new GraphEdge<RemoteResolveResult>(node.OuterEdge, node.Root, dependency);

                        var childNode = new Node()
                        {
                            OuterEdge = innerEdge,
                            DependencyType = typeIntersection,
                            Root = child
                        };

                        nodeQueue.Enqueue(childNode);
                    }
                }

                node = null;
                if (nodeQueue.Count > 0)
                {
                    node = nodeQueue.Dequeue();
                }
            }
        }

        private class Node
        {
            public GraphEdge<RemoteResolveResult> OuterEdge { get; set; }

            public LibraryIncludeType DependencyType { get; set; }

            public GraphItem<RemoteResolveResult> Root { get; set; }
        }

        /// <summary>
        /// Walk the flattened dependency nodes and determine the <see cref="LibraryIncludeType"/>
        /// for each library id.
        /// </summary>
        internal static Dictionary<string, LibraryIncludeType> FlattenDependencyTypes3(
            RestoreTargetGraph targetGraph,
            PackageSpec spec)
        {
            var result = new Dictionary<string, LibraryIncludeType>(StringComparer.OrdinalIgnoreCase);

            var unifiedNodes = new Dictionary<string, GraphItem<RemoteResolveResult>>(StringComparer.OrdinalIgnoreCase);

            // Create a look up table of id -> library
            // This should contain only packages and projects. If there is a project with the 
            // same name as a package, use the project.
            // Projects take precedence over packages here to match the resolver behavior.
            foreach (var item in targetGraph.Flattened
                .OrderBy(lib => OrderType(lib)))
            {
                // Include flags only apply to packages and projects
                if (IsPackageOrProject(item) && !unifiedNodes.ContainsKey(item.Key.Name))
                {
                    unifiedNodes.Add(item.Key.Name, item);
                }
            }

            // Walk all graphs and merge the results
            foreach (var graph in targetGraph.Graphs)
            {
                // The top level edge contains only the root node.
                var outerEdge = new GraphEdge<RemoteResolveResult>(outerEdge: null, item: graph.Item, edge: null);

                foreach (var root in graph.InnerNodes)
                {
                    // Walk only the projects and packages
                    GraphItem<RemoteResolveResult> unifiedRoot;
                    if (unifiedNodes.TryGetValue(root.Key.Name, out unifiedRoot))
                    {
                        // Find the initial project -> dependency flags
                        var typeIntersection = GetDependencyType(graph, root);

                        FlattenDependencyTypesUnified(result, root.Item, outerEdge, unifiedNodes, typeIntersection);
                    }
                }
            }

            // Override flags for direct dependencies
            var directDependencies = spec.Dependencies.ToList();

            // Add dependencies defined under the framework node
            var specFramework = spec.GetTargetFramework(targetGraph.Framework);
            if (specFramework != null && specFramework.Dependencies != null)
            {
                directDependencies.AddRange(specFramework.Dependencies);
            }

            foreach (var dependency in directDependencies)
            {
                if (result.ContainsKey(dependency.Name))
                {
                    result[dependency.Name] = dependency.IncludeType;
                }
                else
                {
                    result.Add(dependency.Name, dependency.IncludeType);
                }
            }

            return result;
        }

        private static void FlattenDependencyTypesUnified(
            Dictionary<string, LibraryIncludeType> result,
            GraphItem<RemoteResolveResult> root,
            GraphEdge<RemoteResolveResult> outerEdge,
            Dictionary<string, GraphItem<RemoteResolveResult>> unifiedNodes,
            LibraryIncludeType dependencyType)
        {
            var rootId = root.Key.Name;

            var hasCycle = false;

            var cursor = outerEdge;
            while (cursor != null)
            {
                if (rootId.Equals(cursor.Item.Key.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // Cycle detected
                    hasCycle = true;
                    break;
                }

                cursor = cursor.OuterEdge;
            }

            if (!hasCycle)
            {
                // Intersect on the way down
                foreach (var dependency in root.Data.Dependencies)
                {
                    // Any nodes that are not in unifiedNodes are types that should be ignored
                    // We should also ignore dependencies that are excluded
                    GraphItem<RemoteResolveResult> child;
                    if (unifiedNodes.TryGetValue(dependency.Name, out child)
                        && !dependency.SuppressParent.Equals(LibraryIncludeType.All))
                    {
                        var typeIntersection = dependencyType.Intersect(dependency.IncludeType)
                            .Except(dependency.SuppressParent);

                        var innerEdge = new GraphEdge<RemoteResolveResult>(outerEdge, root, dependency);

                        FlattenDependencyTypesUnified(result, child, innerEdge, unifiedNodes, typeIntersection);
                    }
                }
            }

            // Combine results on the way up
            LibraryIncludeType currentTypes;
            if (result.TryGetValue(rootId, out currentTypes))
            {
                result[rootId] = currentTypes.Combine(dependencyType);
            }
            else
            {
                result.Add(rootId, dependencyType);
            }
        }

        /// <summary>
        /// Find the flags for a node. 
        /// Include - Exclude - ParentExclude
        /// </summary>
        private static LibraryIncludeType GetDependencyType(
            GraphNode<RemoteResolveResult> parent,
            GraphNode<RemoteResolveResult> child)
        {
            var match = parent.Item.Data.Dependencies.FirstOrDefault(dependency =>
                dependency.Name.Equals(child.Key.Name, StringComparison.OrdinalIgnoreCase));

            Debug.Assert(match != null, "The graph contains a dependency that the node does not list");

            var flags = match.IncludeType;

            // Unless the root project is the grand parent here, the suppress flag should be applied directly to the 
            // child since it has no effect on the parent.
            if (parent.OuterNode != null)
            {
                flags = flags.Except(match.SuppressParent);
            }

            return flags;
        }

        private static bool IsPackageOrProject(GraphItem<RemoteResolveResult> item)
        {
            return item.Key.Type == LibraryTypes.Package
                || item.Key.Type == LibraryTypes.Project
                || item.Key.Type == LibraryTypes.ExternalProject;
        }

        /// <summary>
        /// Prefer projects over packages
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        private static int OrderType(GraphItem<RemoteResolveResult> item)
        {
            switch (item.Key.Type)
            {
                case LibraryTypes.Project:
                    return 0;
                case LibraryTypes.ExternalProject:
                    return 1;
                case LibraryTypes.Package:
                    return 2;
            }

            return 5;
        }
    }
}
