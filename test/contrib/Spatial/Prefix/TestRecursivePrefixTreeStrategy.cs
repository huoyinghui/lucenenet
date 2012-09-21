﻿/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using Lucene.Net.Documents;
using Lucene.Net.Spatial.Prefix;
using Lucene.Net.Spatial.Prefix.Tree;
using Lucene.Net.Spatial.Queries;
using NUnit.Framework;
using Spatial4n.Core.Context;
using Spatial4n.Core.Distance;
using Spatial4n.Core.Shapes;
using Spatial4n.Core.Shapes.Impl;
using Spatial4n.Core.Util;

namespace Lucene.Net.Contrib.Spatial.Test.Prefix
{
	public class TestRecursivePrefixTreeStrategy : StrategyTestCase
	{
		private int maxLength;

		//Tests should call this first.
		private void init(int maxLength)
		{
			this.maxLength = maxLength;
			this.ctx = SpatialContext.GEO;
			var grid = new GeohashPrefixTree(ctx, maxLength);
			this.strategy = new RecursivePrefixTreeStrategy(grid, GetType().Name);
		}

		[Test]
		public void testFilterWithVariableScanLevel()
		{
			init(GeohashPrefixTree.GetMaxLevelsPossible());
			getAddAndVerifyIndexedDocuments(DATA_WORLD_CITIES_POINTS);

			//execute queries for each prefix grid scan level
			for (int i = 0; i <= maxLength; i++)
			{
				((RecursivePrefixTreeStrategy)strategy).SetPrefixGridScanLevel(i);
				executeQueries(SpatialMatchConcern.FILTER, QTEST_Cities_IsWithin_BBox);
			}
		}

        [Test]
        public void testOneMeterPrecision()
        {
            init(GeohashPrefixTree.GetMaxLevelsPossible());
            GeohashPrefixTree grid = (GeohashPrefixTree) ((RecursivePrefixTreeStrategy) strategy).GetGrid();
            //DWS: I know this to be true.  11 is needed for one meter
            assertEquals(11, grid.GetLevelForDistance(ctx.GetDistCalc().DistanceToDegrees(0.001)));
        }

        [Test]
        public void testPrecision()
        {
            init(GeohashPrefixTree.GetMaxLevelsPossible());

            Point iPt = ctx.MakePoint(2.8028712999999925, 48.3708044); //lon, lat
            addDocument(newDoc("iPt", iPt));
            commit();

            Point qPt = ctx.MakePoint(2.4632387000000335, 48.6003516);

            const double DIST = 35.75; //35.7499...
            assertEquals(DIST, ctx.GetDistCalc().Distance(iPt, qPt), 0.001);

            //distErrPct will affect the query shape precision. The indexed precision
            // was set to nearly zilch via init(GeohashPrefixTree.getMaxLevelsPossible());
            const double distErrPct = 0.025; //the suggested default, by the way
            const double distMult = 1 + distErrPct;

            assertTrue(35.74*distMult >= DIST);
            checkHits(q(qPt, 35.74, distErrPct), 1, null);

            assertTrue(30*distMult < DIST);
            checkHits(q(qPt, 30, distErrPct), 0, null);

            assertTrue(33*distMult < DIST);
            checkHits(q(qPt, 33, distErrPct), 0, null);

            assertTrue(34*distMult < DIST);
            checkHits(q(qPt, 34, distErrPct), 0, null);
        }

	    [Test]
		public void geohashRecursiveRandom()
		{
			init(12);
			var random = NewRandom();

			//1. Iterate test with the cluster at some worldly point of interest
            var clusterCenters = new Point[] { ctx.MakePoint(-180, 0), ctx.MakePoint(0, 90), ctx.MakePoint(0, -90) };
			foreach (var clusterCenter in clusterCenters)
			{
				//2. Iterate on size of cluster (a really small one and a large one)
				String hashCenter = GeohashUtils.EncodeLatLon(clusterCenter.GetY(), clusterCenter.GetX(), maxLength);
				//calculate the number of degrees in the smallest grid box size (use for both lat & lon)
				String smallBox = hashCenter.Substring(0, hashCenter.Length - 1);//chop off leaf precision
				Rectangle clusterDims = GeohashUtils.DecodeBoundary(smallBox, ctx);
				double smallDegrees = Math.Max(clusterDims.GetMaxX() - clusterDims.GetMinX(), clusterDims.GetMaxY() - clusterDims.GetMinY());
				Assert.IsTrue(smallDegrees < 1);
				const double largeDegrees = 20d; //good large size; don't use >=45 for this test code to work
				double[] sideDegrees = { largeDegrees, smallDegrees };
				foreach (double sideDegree in sideDegrees)
				{
					//3. Index random points in this cluster box
					deleteAll();
					var points = new List<Point>();
					for (int i = 0; i < 20; i++)
					{
						double x = random.NextDouble() * sideDegree - sideDegree / 2 + clusterCenter.GetX();
						double y = random.NextDouble() * sideDegree - sideDegree / 2 + clusterCenter.GetY();
						Point pt = normPointXY(x, y);
						points.Add(pt);
						addDocument(newDoc("" + i, pt));
					}
					commit();

					//3. Use 4 query centers. Each is radially out from each corner of cluster box by twice distance to box edge.
					foreach (double qcXoff in new double[] { sideDegree, -sideDegree })
					{//query-center X offset from cluster center
						foreach (double qcYoff in new double[] { sideDegree, -sideDegree })
						{//query-center Y offset from cluster center
							Point queryCenter = normPointXY(qcXoff + clusterCenter.GetX(),
								qcYoff + clusterCenter.GetY());
							double[] distRange = calcDistRange(queryCenter, clusterCenter, sideDegree);
							//4.1 query a small box getting nothing
                            checkHits(q(queryCenter, distRange[0] * 0.99), 0, null);
							//4.2 Query a large box enclosing the cluster, getting everything
                            checkHits(q(queryCenter, distRange[1] * 1.01), points.Count, null);
							//4.3 Query a medium box getting some (calculate the correct solution and verify)
							double queryDist = distRange[0] + (distRange[1] - distRange[0]) / 2;//average

							//Find matching points.  Put into int[] of doc ids which is the same thing as the index into points list.
							int[] ids = new int[points.Count];
							int ids_sz = 0;
							for (int i = 0; i < points.Count; i++)
							{
								Point point = points[i];
								if (ctx.GetDistCalc().Distance(queryCenter, point) <= queryDist)
									ids[ids_sz++] = i;
							}
							
							var ids_new = new int[ids_sz]; // will pad with 0's if larger
							Array.Copy(ids, ids_new, ids_sz);
							ids = ids_new;
							//assert ids_sz > 0 (can't because randomness keeps us from being able to)

                            checkHits(q(queryCenter, queryDist), ids.Length, ids);
						}
					}

				}//for sideDegree

			}//for clusterCenter

		}//randomTest()

        private SpatialArgs q(Point pt, double dist, double distErrPct = 0.0)
        {
            Shape shape = ctx.MakeCircle(pt, dist);
            var args = new SpatialArgs(SpatialOperation.Intersects, shape);
            args.DistErrPct = distErrPct;
            return args;
        }

        private void checkHits(SpatialArgs args, int assertNumFound, int[] assertIds)
		{
			SearchResults got = executeQuery(strategy.MakeQuery(args), 100);
            assertEquals("" + args, assertNumFound, got.numFound);
			if (assertIds != null)
			{
				var gotIds = new HashSet<int>();
				foreach (SearchResult result in got.results)
				{
					gotIds.Add(int.Parse(result.document.Get("id")));
				}
				foreach (int assertId in assertIds)
				{
					Assert.True(gotIds.Contains(assertId), "has " + assertId);
				}
			}
		}

		private Document newDoc(String id, Shape shape)
		{
			Document doc = new Document();
			doc.Add(new Field("id", id, Field.Store.YES, Field.Index.ANALYZED));
			foreach (var f in strategy.CreateIndexableFields(shape))
			{
				doc.Add(f);
			}
			if (storeShape)
				doc.Add(new Field(strategy.GetFieldName(), ctx.ToString(shape), Field.Store.YES, Field.Index.ANALYZED));
			return doc;
		}

		private double[] calcDistRange(Point startPoint, Point targetCenter, double targetSideDegrees)
		{
			double min = Double.MaxValue;
			double max = Double.MinValue;
			foreach (double xLen in new double[] { targetSideDegrees, -targetSideDegrees })
			{
				foreach (double yLen in new double[] { targetSideDegrees, -targetSideDegrees })
				{
					Point p2 = normPointXY(targetCenter.GetX() + xLen / 2, targetCenter.GetY() + yLen / 2);
					double d = ctx.GetDistCalc().Distance(startPoint, p2);
					min = Math.Min(min, d);
					max = Math.Max(max, d);
				}
			}
			return new double[] { min, max };
		}

		/** Normalize x & y (put in lon-lat ranges) & ensure geohash round-trip for given precision. */
		private Point normPointXY(double x, double y)
		{
			//put x,y as degrees into double[] as radians
			double[] latLon = { y * DistanceUtils.DEG_180_AS_RADS, DistanceUtils.ToRadians(x) };
			DistanceUtils.NormLatRAD(latLon);
			DistanceUtils.NormLatRAD(latLon);
			double x2 = DistanceUtils.ToDegrees(latLon[1]);
			double y2 = DistanceUtils.ToDegrees(latLon[0]);
			//overwrite latLon, units is now degrees

			return GeohashUtils.Decode(GeohashUtils.EncodeLatLon(y2, x2, maxLength), ctx);
		}

	}
}
