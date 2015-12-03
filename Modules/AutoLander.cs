//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	public class AutoLander : TCAModule
	{
		public class Config : ModuleConfig
		{
			new public const string NODE_NAME = "LND";

			[Persistent] public float MaxUnevenness        = 0.1f;
			[Persistent] public float UnevennessThreshold  = 0.3f;
			[Persistent] public float MaxHorizontalTime    = 5f;
			[Persistent] public float MinVerticalSpeed     = 0.1f;
			[Persistent] public float WideCheckAltitude    = 200f;
			[Persistent] public float MaxWideCheckAltitude = 1000f;
			[Persistent] public int   WideCheckLevel       = 5;
			[Persistent] public float NodeTargetRange      = 15;
			[Persistent] public float NodeAnchorF          = 0.5f;
			[Persistent] public float GearOnAtH            = 5;
			[Persistent] public float StopAtAlt            = 2;
			[Persistent] public float StopTimer            = 2;
			[Persistent] public float CutoffTimer          = 2;

			public float MaxStartAltitude;

			public override void Init ()
			{
				base.Init ();
				MaxStartAltitude = MaxWideCheckAltitude/2;
			}
		}
		static Config LND { get { return TCAScenario.Globals.LND; } }

		static int RadarMask = (1 << LayerMask.NameToLayer("Local Scenery") | 1 << LayerMask.NameToLayer("Parts") | 1);
		enum Stage { None, Start, PointCheck, WideCheck, FlatCheck, MoveNext, Land }

		Stage stage;
		IEnumerator scanner;
		SurfaceNode[,] Nodes;
		readonly List<SurfaceNode> FlatNodes = new List<SurfaceNode>();
		HashSet<SurfaceNode> TriedNodes;
		SurfaceNode NextNode, FlattestNode;
		int side, bside, center, total_rays, done_rays;
		Vector3 right, fwd, up, down, dir, sdir, anchor;
		float MaxDistance, delta;
		float DesiredAltitude;
		bool landing_started;
		readonly Timer StopTimer = new Timer();
		readonly Timer CutoffTimer = new Timer();

		public float Progress { get { return scanner != null? done_rays/(float)total_rays : 0f; } }

		public AutoLander(ModuleTCA tca) { TCA = tca; }

		public override void Init()
		{
			base.Init();
			StopTimer.Period = LND.StopTimer;
			CutoffTimer.Period = LND.CutoffTimer;
			CFG.AP.AddHandler(this, Autopilot.Land);
			TriedNodes = new HashSet<SurfaceNode>(new SurfaceNode.Comparer(VSL.R));
			#if DEBUG
			RenderingManager.AddToPostDrawQueue(1, RadarBeam);
			#endif
		}

		protected override void UpdateState() 
		{ IsActive = VSL.OnPlanet && CFG.AP[Autopilot.Land]; }

		public void LandCallback(Multiplexer.Command cmd)
		{
			stage = Stage.None;
			scanner = null;
			NextNode = null;
			Nodes = null;
			StopTimer.Reset();
			CutoffTimer.Reset();
			landing_started = false;

			switch(cmd)
			{
			case Multiplexer.Command.On:
			case Multiplexer.Command.Resume:
				if(VSL.LandedOrSplashed) { CFG.AP.OffIfOn(Autopilot.Land); break; }
				CFG.HF.On(HFlight.Stop);
				DesiredAltitude = 0;
				TriedNodes = new HashSet<SurfaceNode>(new SurfaceNode.Comparer(VSL.R));
				break;

			case Multiplexer.Command.Off:
				CFG.VF.On(VFlight.AltitudeControl);
				DesiredAltitude = VSL.Altitude;
				break;
			}
		}

		SurfaceNode get_surface_node(float radius)
		{
			RaycastHit raycastHit;
			var c = VSL.C+dir*(VSL.R+0.1f);
			if(Physics.SphereCast(c, radius, dir, out raycastHit, MaxDistance, RadarMask))
			{
				if(VSL.mainBody.ocean && 
				   (raycastHit.point-VSL.mainBody.position).magnitude < VSL.mainBody.Radius)
					return null;
				return new SurfaceNode(c, raycastHit.point, up);
			}
			return null;
		}

		IEnumerator surface_scanner(int lvl, SurfaceNode start = null, float d = 0)
		{
			if(VSL.refT == null) yield break;
			//initialize the system
			side   = lvl*2-1;
			bside  = side+2;
			center = (bside-1)/2;
			up     = VSL.Up;
			down   = -VSL.Altitude*up;
			right  = Vector3d.Cross(VSL.Up, VSL.Fwd).normalized;
			fwd    = Vector3.Cross(right, up);
			Nodes  = new SurfaceNode[bside,bside];
			delta  = d > 0? d : DesiredAltitude/bside*lvl;
			anchor = Vector3.zero;
			if(start == null) anchor = VSL.wCoM+down;
			else anchor = start.position;
			//job progress
			total_rays = bside*bside+side*side+1;
			done_rays = 0;
//			Utils.Log("Scanning Surface: {0}x{0} -> {1}x{1}", bside, side);//debug
			yield return null;
			//cast the rays
			for(int i = 0; i < bside; i++)
				for(int j = 0; j < bside; j++)
				{
					sdir = anchor-VSL.wCoM;
					MaxDistance = sdir.magnitude * 10;
					dir = (sdir+right*(i-center)*delta+fwd*(j-center)*delta).normalized;
					Nodes[i,j] = get_surface_node(delta/2);
					done_rays++;
					yield return null;
				}
			//compute unevenness
			for(int i = 1; i <= side; i++)
				for(int j = 1; j <= side; j++)
				{
					var n = Nodes[i,j];
					if(n == null) continue;
					for(int u = i-1; u <= i+1; u += 2)
						for(int v = j-1; v <= j+1; v += 2)
						{
							var n1 = Nodes[u,v];
							if(n1 == null) continue;
							n.UpdateUnevenness(n1);
						}
					done_rays++;
					yield return null;
				}
			if(lvl > 1)
			{
				FlatNodes.Clear();
				for(int i = 1; i <= side; i++)
					for(int j = 1; j <= side; j++)
					{
						var n = Nodes[i,j];
						if(n != null && n.flat && !TriedNodes.Contains(n))
							FlatNodes.Add(n);
					}
				done_rays++;
				yield return null;
				FlatNodes.Sort((n1, n2) => n1.distance.CompareTo(n2.distance));
			}
			#if DEBUG
//			print_nodes();
			#endif
		}

		SurfaceNode flattest_node 
		{
			get 
			{
				if(Nodes == null) return null;
				SurfaceNode flattest = null;
				for(int i = 1; i <= side; i++)
					for(int j = 1; j <= side; j++)
					{
						var n = Nodes[i,j];
						if(n == null) continue;
						if(TriedNodes.Contains(n)) continue;
						if(flattest == null) flattest = n;
						else if(flattest.unevenness > n.unevenness)
							flattest = n;
					}
				return flattest;
			}
		}

		SurfaceNode center_node
		{ get { return Nodes == null ? null : Nodes[center, center]; } }

		bool altitude_changed
		{
			get
			{
				CFG.AltitudeAboveTerrain = true;
				if(DesiredAltitude <= 0) 
				{
					VSL.UpdateAltitudeInfo();
					DesiredAltitude = VSL.Altitude;
				}
				CFG.DesiredAltitude = DesiredAltitude;
				var err = Mathf.Abs(VSL.Altitude-DesiredAltitude);
				if(err > 10)
				{
					CFG.VF.OnIfNot(VFlight.AltitudeControl);
					return false;
				}
				else if(err < 5)
				{
					CFG.VF.OffIfOn(VFlight.AltitudeControl);
					CFG.VerticalCutoff = 0;
				}
				return Mathf.Abs(VSL.AbsVerticalSpeed) < LND.MinVerticalSpeed;
			}
		}

		bool stopped
		{
			get
			{
				CFG.HF.OnIfNot(HFlight.Stop);
				if(VSL.R/VSL.HorizontalSpeed > LND.MaxHorizontalTime)
					return StopTimer.Check;
				else StopTimer.Reset();
				return false;
			}
		}

		bool fully_stopped 
		{ 
			get 
			{ 
				var s = stopped;
				var a = altitude_changed;
				return s && a;
			} 
		}

		bool moved_to_next_node
		{
			get
			{
				CFG.AltitudeAboveTerrain = true;
				CFG.VF.OnIfNot(VFlight.AltitudeControl);
				if(CFG.Anchor == null) { CFG.AP.Off(); return false; }
				if(CFG.Anchor.DistanceTo(VSL.vessel)-VSL.R < CFG.Anchor.Distance)
				{
					if(NextNode.flat)
					{
						CFG.Nav.OnIfNot(Navigation.Anchor);
						if(CFG.Anchor.DistanceTo(VSL.vessel) < delta)
							return StopTimer.Check;
					}
					else return true;
				}
				else if(!CFG.Nav[Navigation.Anchor] && 
				        (!CFG.Nav[Navigation.GoToTarget] || VSL.Target != CFG.Anchor))
				{
					SetTarget(CFG.Anchor);
					CFG.Nav.On(Navigation.GoToTarget);
				}
				StopTimer.Reset();
				return false;
			}
		}

		float distance_to_node(SurfaceNode n)
		{ return Vector3.ProjectOnPlane(n.position-VSL.wCoM, VSL.Up).magnitude; }

		void wide_check(float delta_alt = 0)
		{
			if(DesiredAltitude < LND.WideCheckAltitude)
				DesiredAltitude = LND.WideCheckAltitude;
			DesiredAltitude += delta_alt;
			if(DesiredAltitude > LND.MaxWideCheckAltitude)
			{
				ScreenMessages.PostScreenMessage("Unable to find suitale place for landing",
				                                 5, ScreenMessageStyle.UPPER_CENTER);
				CFG.AP.Off();
			}
			else stage = Stage.WideCheck;
		}

		void try_move_to_flattest()
		{
			NextNode = FlattestNode;
			if(NextNode != null && 
			   distance_to_node(NextNode) > LND.NodeTargetRange*2 && 
			   NextNode.unevenness > LND.UnevennessThreshold) 
				move_next();
			else wide_check(LND.WideCheckAltitude);
		}

		void move_next()
		{
			CFG.Anchor = new WayPoint(VSL.mainBody.GetLatitude(NextNode.position),
			                          VSL.mainBody.GetLongitude(NextNode.position));
			CFG.Anchor.Distance = LND.NodeTargetRange;
			stage = Stage.MoveNext;
		}

		void land()
		{
			CFG.HF.Off();
			var c = center_node;
			CFG.Anchor = new WayPoint(VSL.mainBody.GetLatitude(c.position),
			                          VSL.mainBody.GetLongitude(c.position));
			CFG.Anchor.Distance = LND.NodeTargetRange;
			CFG.Nav.On(Navigation.Anchor);
			DesiredAltitude = LND.GearOnAtH+VSL.H;
			stage = Stage.Land;
		}

		bool scan(int lvl, SurfaceNode start = null, float d = 0)
		{
			if(scanner == null) scanner = surface_scanner(lvl, start, d);
			if(scanner.MoveNext()) return true;
			scanner = null;
			return false;
		}

		void gear_on()
		{
			if(!VSL.ActionGroups[KSPActionGroup.Gear])
				VSL.ActionGroups.SetGroup(KSPActionGroup.Gear, true);
		}

		protected override void Update()
		{
			if(!IsActive || CFG.AP.Paused) return;
			switch(stage)
			{
			case Stage.None:
				CFG.AltitudeAboveTerrain = true;
				CFG.VF.OnIfNot(VFlight.AltitudeControl);
				if(DesiredAltitude <= 0) 
				{   //here we just need the altitude control to prevent smashing into something while stopping
					VSL.UpdateAltitudeInfo();
					DesiredAltitude = VSL.Altitude > LND.MaxStartAltitude? LND.MaxStartAltitude : VSL.Altitude;
					CFG.DesiredAltitude = DesiredAltitude;
				}
				if(stopped && VSL.Altitude < LND.MaxStartAltitude+10) 
				{
					DesiredAltitude = VSL.Altitude;
					stage = Stage.PointCheck;	
				}
				break;
			case Stage.PointCheck:
				SetState(TCAState.CheckingSite);
				if(!fully_stopped) break;
				if(scan(1, null, VSL.R*2)) break;
				if(Nodes == null || center_node == null) 
				{ wide_check(); break; }
				TriedNodes.Add(center_node);
				if(!center_node.flat) wide_check();
				else land();
				break;
			case Stage.FlatCheck:
				if(!fully_stopped) break;
				if(FlatNodes.Count > 0)
				{
					if(scan(1, FlatNodes[0], VSL.R*2)) break;
					FlatNodes.RemoveAt(0);
					var c = center_node;
					if(c == null || !c.flat) break;
					NextNode = c;
					move_next();
					break;
				}
				else try_move_to_flattest();
				break;
			case Stage.WideCheck:
				if(!fully_stopped) 
				{ SetState(TCAState.Searching); break; }
				SetState(TCAState.Scanning);
				if(scan(LND.WideCheckLevel)) break;
				FlattestNode = flattest_node;
				if(FlatNodes.Count > 0) 
				{ stage = Stage.FlatCheck; break; }
				else try_move_to_flattest();
				break;
			case Stage.MoveNext:
				SetState(NextNode.flat? TCAState.CheckingSite : TCAState.Searching);
				if(!moved_to_next_node) break;
				DesiredAltitude = VSL.Altitude;
				if(NextNode.flat) stage = Stage.PointCheck;
				else wide_check();
				break;
			case Stage.Land:
				SetState(TCAState.Landing);
				if(!landing_started)
				{
					landing_started = true;
					SquadAction(vsl => vsl.CFG.AP.XOnIfNot(Autopilot.Land));
				}
				if(DesiredAltitude > 0)
				{
					CFG.Nav.OnIfNot(Navigation.Anchor);
					DesiredAltitude = VSL.H*LND.GearOnAtH;
					if(VSL.Altitude > DesiredAltitude &&
					   !altitude_changed) break;
					CFG.VerticalCutoff = -0.5f;
					DesiredAltitude = -10;
				}
				gear_on();
				CFG.VF.Off();
				if(VSL.LandedOrSplashed) 
				{ 
					if(!CutoffTimer.Check) break;
					CFG.AP.XOff(); 
					CFG.VerticalCutoff = -10; 
					CFG.VF.On(VFlight.AltitudeControl);
				}
				else
				{
					if(VSL.Altitude > LND.StopAtAlt+VSL.H)
						CFG.Nav.OnIfNot(Navigation.Anchor);
					else 
					{
						CFG.Nav.OffIfOn(Navigation.Anchor);
						CFG.HF.OnIfNot(HFlight.Stop);
					}
					set_VSpeed(VSL.SlowThrust? -0.5f :
					           Mathf.Lerp(-0.5f, -1, VSL.Altitude/(VSL.H*LND.GearOnAtH)));
				}
				CutoffTimer.Reset();
				break;
			default: 
				CFG.AP.Off();
				break;
			}
		}

		void set_VSpeed(float val)
		{ CFG.VerticalCutoff = Mathf.Lerp(CFG.VerticalCutoff, val, TimeWarp.fixedDeltaTime); }

		#if DEBUG
		public void RadarBeam()
		{
			if(!CFG.AP[Autopilot.Land]) return;
			if(Nodes == null) return;
			if(scanner == null && NextNode != null)
			{
				GLUtils.GLLine(VSL.wCoM, NextNode.position, Color.Lerp(Color.blue, Color.red, NextNode.unevenness));
				return;
			}
			for(int i = 0; i < bside; i++)
				for(int j = 0; j < bside; j++)
				{
					var n = Nodes[i,j];
					if(n == null) continue;
					GLUtils.GLLine(VSL.wCoM, n.position, Color.Lerp(Color.blue, Color.red, n.unevenness));
				}
		}

		public override void Reset()
		{
			base.Reset();
			RenderingManager.RemoveFromPostDrawQueue(1, RadarBeam);
		}

		void print_nodes()
		{
			if(Nodes == null) return;
			var nodes = string.Format("Nodes: {0}x{0}\n", bside);
			for(int i = 0; i < bside; i++)
				for(int j = 0; j < bside; j++)
				{
					var n = Nodes[i,j];
					nodes += string.Format("[{0},{1}]: pos ", i, j);
					if(n == null) { nodes += "null\n"; continue; }
					nodes += Utils.formatVector(n.position) + "\n";
					nodes += string.Format("unevenness: {0}", n.unevenness) + "\n";
					nodes += string.Format("flat: {0}", n.flat) + "\n";
				}
			Utils.Log(nodes+"\n");
		}
		#endif

		class SurfaceNode
		{
			internal class Comparer : IEqualityComparer<SurfaceNode>
			{
				readonly float threshold;

				public Comparer(float threshold) { this.threshold = threshold; }

				public bool Equals(SurfaceNode n1, SurfaceNode n2)
				{ return (n1.position-n2.position).magnitude < threshold; }

				public int GetHashCode(SurfaceNode n) 
				{ return (int)(n.position.x/threshold) ^ (int)(n.position.y/threshold) ^ (int)(n.position.z/threshold); }
			}

			Vector3 up;
			public Vector3 position;
			public float unevenness;
			public float distance;
			public bool flat;
			public readonly  HashSet<SurfaceNode> neighbours = new HashSet<SurfaceNode>();

			public SurfaceNode(Vector3 ori, Vector3 pos, Vector3 up) 
			{ 
				distance = Vector3.ProjectOnPlane(pos-ori, up).magnitude; 
				position = pos;
				this.up  = up; 
			}

			public float DistanceTo(SurfaceNode n)
			{ return (n.position-position).magnitude; }

			public void UpdateUnevenness(SurfaceNode n)
			{
				if(neighbours.Contains(n)) return;
				var unev = Mathf.Abs(Vector3.Dot((n.position-position).normalized, up));
				unevenness += unev;
				n.unevenness += unev;
				neighbours.Add(n);
				n.neighbours.Add(this);
				flat = unevenness < LND.MaxUnevenness;
			}
		}
	}
}

