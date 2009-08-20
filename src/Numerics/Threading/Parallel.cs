﻿// <copyright file="Parallel.cs" company="Math.NET">
// Math.NET Numerics, part of the Math.NET Project
// http://mathnet.opensourcedotnet.info
//
// Copyright (c) 2009 Math.NET
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
// </copyright>

namespace MathNet.Numerics.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using Properties;

    /// <summary>
    /// Provides support for parallel loops. 
    /// </summary>
    internal static class Parallel
    {
        /// <summary>
        /// Executes a for loop in which iterations may run in parallel. 
        /// </summary>
        /// <param name="fromInclusive">The start index, inclusive.</param>
        /// <param name="toExclusive">The end index, exclusive.</param>
        /// <param name="body">The body to be invoked for each iteration.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="body"/> argument is null.</exception>
        /// <exception cref="AggregateException">At least one invocation of the body threw an exception.</exception>
        public static void For(int fromInclusive, int toExclusive, Action<int> body)
        {
            if (body == null)
            {
                throw new ArgumentNullException("body");
            }

            // fast forward execution if it's only one or none items
            var count = toExclusive - fromInclusive;
            if (count <= 1)
            {
                if (count == 1)
                {
                    body(fromInclusive);
                }

                return;
            }

            // fast forward execution in case parallelization is disabled
            if (Control.DisableParallelization
                || ThreadQueue.ThreadCount <= 1
                || ThreadQueue.IsInWorkerThread)
            {
                for (int i = fromInclusive; i < toExclusive; i++)
                {
                    body(i);
                }

                return;
            }

            var actions = new Action[ThreadQueue.ThreadCount];
            var size = count / actions.Length;

            // partition the jobs into separate sets for each but the last worked thread
            for (var i = 0; i < actions.Length - 1; i++)
            {
                var start = fromInclusive + (i * size);
                var stop = fromInclusive + ((i + 1) * size);

                actions[i] =
                    () =>
                    {
                        for (int j = start; j < stop; j++)
                        {
                            body(j);
                        }
                    };
            }

            // add another set for last worker thread
            actions[actions.Length - 1] =
                () =>
                {
                    for (int i = fromInclusive + ((actions.Length - 1) * size); i < toExclusive; i++)
                    {
                        body(i);
                    }
                };

            Invoke(actions);
        }

        /// <summary>
        /// Executes a for each operation on an IEnumerable{T} in which iterations may run in parallel.
        /// </summary>
        /// <typeparam name="T">The type of the data in the source.</typeparam>
        /// <param name="source">An enumerable data source.</param>
        /// <param name="body">The delegate that is invoked once per iteration.</param>
        public static void ForEach<T>(IEnumerable<T> source, Action<T> body)
        {
            if (body == null)
            {
                throw new ArgumentNullException("body");
            }

            // source is a IList, call For instead.
            if (source is IList<T>)
            {
                var list = (IList<T>)source;
                For(0, list.Count, i => body(list[i]));
                return;
            }

            // fast forward execution in case parallelization is disabled
            if (Control.DisableParallelization
                || ThreadQueue.ThreadCount <= 1
                || ThreadQueue.IsInWorkerThread)
            {
                foreach (var item in source)
                {
                    body(item);
                }

                return;
            }

            var enumerator = source.GetEnumerator();
            var maxBlockSize = Control.InitialThreadBlockSize;
            var scalingFactor = Control.BlockScalingFactor;
            var tasks = new List<Task>();
            while (enumerator.MoveNext())
            {
                var pos = 0;
                var list = new T[maxBlockSize];
                list[pos++] = enumerator.Current;

                var count = 1;
                while (count < maxBlockSize && enumerator.MoveNext())
                {
                    list[pos++] = enumerator.Current;
                    count++;
                }
               
                var task = new Task(
                    () =>
                    {
                        for (var i = 0; i < pos; i++)
                        {
                            body(list[i]);
                        }
                    });

                ThreadQueue.Enqueue(task);
                
                maxBlockSize  = Math.Min(Control.MaximumBlockSize, maxBlockSize * scalingFactor);
            }

            WaitForTasksToComplete(tasks.ToArray());
            CollectExceptionsAndDisposeTasks(tasks);
        }

        /// <summary>
        /// Executes each of the provided actions inside a discrete, asynchronous task. 
        /// </summary>
        /// <param name="actions">An array of actions to execute.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="actions"/> argument is null.</exception>
        /// <exception cref="ArgumentException">The actions array contains a null element.</exception>
        /// <exception cref="AggregateException">An action threw an exception.</exception>
        public static void Run(params Action[] actions)
        {
            if (actions == null)
            {
                throw new ArgumentNullException("actions");
            }

            // fast forward execution if it's only one or none items
            if (actions.Length <= 1)
            {
                if (actions.Length == 1)
                {
                    actions[0]();
                }

                return;
            }

            // fast forward execution in case parallelization is disabled
            if (Control.DisableParallelization
                || ThreadQueue.ThreadCount <= 1
                || ThreadQueue.IsInWorkerThread)
            {
                for (int i = 0; i < actions.Length; i++)
                {
                    actions[i]();
                }

                return;
            }

            Invoke(actions);
        }

        /// <summary>
        /// Executes each of the provided actions inside a discrete, asynchronous task. 
        /// </summary>
        /// <param name="actions">An array of actions to execute.</param>
        /// <exception cref="ArgumentException">The actions array contains a null element.</exception>
        /// <exception cref="AggregateException">An action threw an exception.</exception>
        private static void Invoke(params Action[] actions)
        {
            // create a job for each action
            var tasks = new Task[actions.Length];
            for (int i = 0; i < tasks.Length; i++)
            {
                Action action = actions[i];
                if (action == null)
                {
                    throw new ArgumentException(String.Format(Resources.ArgumentItemNull, "actions"), "actions");
                }

                tasks[i] = new Task(action);
            }

            // run the jobs
            ThreadQueue.Enqueue(tasks);

            WaitForTasksToComplete(tasks);

            CollectExceptionsAndDisposeTasks(tasks);
        }

        /// <summary>
        /// Waits for tasks to complete.
        /// </summary>
        /// <param name="tasks">The tasks.</param>
        private static void WaitForTasksToComplete(Task[] tasks)
        {
            // wait until all tasks have  been completed
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                // not sure if this the best approach for STA
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i].WaitOne();
                }
            }
            else
            {
                WaitHandle.WaitAll(tasks);
            }
        }

        /// <summary>
        /// Collects the exceptions and dispose tasks.
        /// </summary>
        /// <param name="tasks">The tasks.</param>
        private static void CollectExceptionsAndDisposeTasks(IEnumerable<Task> tasks)
        {
            // collect all thrown exceptions and dispose the jobs
            var exceptions = new List<Exception>();
            foreach (var task in tasks)
            {
                if (task.ThrewException)
                {
                    exceptions.Add(task.Exception);
                }

                // this calls dispose
                task.Close();
            }

            // throw the aggregated exceptions, if any
            if (exceptions.Count > 0)
            {
                throw new AggregateException(exceptions);
            }
        }
    }
}