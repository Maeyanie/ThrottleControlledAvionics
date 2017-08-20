//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using System.Collections.Generic;
using UnityEngine;
using AT_Utils;

namespace ThrottleControlledAvionics
{
	public abstract class ThrustDirectionControl : AutopilotModule
	{
		public class Config : ModuleConfig
		{
			[Persistent] public float TWRf  = 3;
			[Persistent] public float VSf   = 3;
		}
		static Config TDC { get { return Globals.Instance.TDC; } }

		protected ThrustDirectionControl(ModuleTCA tca) : base(tca) {}
	}

	[CareerPart]
	[RequireModules(typeof(AttitudeControl),
	                typeof(BearingControl),
	                typeof(SASBlocker))]
	[OptionalModules(typeof(TranslationControl))]
	public class HorizontalSpeedControl : ThrustDirectionControl
	{
		public new class Config : ModuleConfig
		{
            public class ManualTranslationConfig : ConfigNodeObject
            {
                [Persistent] public PIDf_Controller PID = new PIDf_Controller(0.5f, 0, 0.5f, 0, 1);
                [Persistent] public float ThrustF = 11.47f;
                [Persistent] public float I_MinSpeed = 20f;
                [Persistent] public float D_Max = 2;
            }

			[Persistent] public float TranslationUpperThreshold  = 5f;
			[Persistent] public float TranslationLowerThreshold  = 0.2f;

			[Persistent] public float RotationLowerThreshold     = 0.01f;
			[Persistent] public float RotationUpperThreshold     = 30f;

			[Persistent] public float TranslationMaxAngle        = 80f;
			[Persistent] public float RotationMaxAngle           = 15f;

			[Persistent] public float HVCurve = 2;
			[Persistent] public float MinHVCurve = 0.5f;
			[Persistent] public float SlowTorqueF = 2;
			[Persistent] public float AccelerationFactor = 1f, MinHvThreshold = 10f;
			[Persistent] public float LowPassF = 0.1f;

			[Persistent] public float MaxCorrectionWeight = 1f;

            [Persistent] public ManualTranslationConfig ManualTranslation = new ManualTranslationConfig();

            [Persistent] public PIDf_Controller3 NeededThrustPID = new PIDf_Controller3(1, 0, 0, -1, 1, 1);
            [Persistent] public float TurnTime_Curve = 1.1f;

            public float TranslationMaxCos;
            public float RotationMaxCos;

			public override void Init() 
			{ 
				base.Init();
				TranslationMaxCos = Mathf.Cos(TranslationMaxAngle*Mathf.Deg2Rad);
				RotationMaxCos = Mathf.Cos(RotationMaxAngle*Mathf.Deg2Rad);
			}
		}
		static Config HSC { get { return Globals.Instance.HSC; } }

		public HorizontalSpeedControl(ModuleTCA tca) : base(tca) {}

		//modules
		BearingControl BRC;
		AttitudeControl ATC;
		TranslationControl TRA;

		readonly PIDf_Controller translation_pid = new PIDf_Controller();
        readonly PIDf_Controller3 needed_thrust_pid = new PIDf_Controller3();
        readonly LowPassFilterVd output_filter = new LowPassFilterVd();
		Vector3d needed_thrust_dir;

		readonly List<Vector3d> CourseCorrections = new List<Vector3d>();
		Vector3d CourseCorrection;

		public override void Init() 
		{ 
			base.Init(); 
			output_filter.Tau = HSC.LowPassF;
			translation_pid.setPID(HSC.ManualTranslation.PID);
            needed_thrust_pid.setPID(HSC.NeededThrustPID);
			CFG.HF.AddSingleCallback(ControlCallback);
		}

		public void AddRawCorrection(Vector3d cor) 
		{ CourseCorrections.Add(cor); }

		public void AddWeightedCorrection(Vector3d cor) 
		{ 
			var cm = cor.magnitude;
			if(cm > 1e-10) cor *= Math.Sqrt(1/cm);
			if(VSL.Physics.G > 1e-10) cor *= Utils.ClampH(Utils.G0/VSL.Physics.G, HSC.MaxCorrectionWeight);
			CourseCorrections.Add(cor);
		}

		#if DEBUG
        public bool UseOldPid = true;
		public void DrawDebugLines()
		{
			if(VSL == null || VSL.vessel == null || VSL.refT == null || !CFG.HF) return;
			Utils.GLVec(VSL.refT.position, VSL.HorizontalSpeed.NeededVector, Color.yellow);
			Utils.GLVec(VSL.refT.position+VSL.Physics.Up*VSL.Geometry.H, VSL.HorizontalSpeed.Vector, Color.red);
			Utils.GLVec(VSL.refT.position+VSL.Physics.Up*VSL.Geometry.H*1.1, CourseCorrection, Color.green);
		}
		#endif

		public override void ClearFrameState() { CourseCorrections.Clear(); }

		protected override void UpdateState() 
		{ 
			base.UpdateState();
			IsActive &= VSL.OnPlanet && CFG.HF && VSL.refT != null; 
			if(IsActive) return;
			if(VSL.Controls.ManualTranslationSwitch.On)
				EnableManualTranslation(false);
		}

		public void ControlCallback(Multiplexer.Command cmd)
		{
			translation_pid.Reset();
            needed_thrust_pid.Reset();
			switch(cmd)
			{
			case Multiplexer.Command.Resume:
				RegisterTo<SASBlocker>();
				NeedCPSWhenMooving();
				break;

			case Multiplexer.Command.On:
				VSL.UpdateOnPlanetStats();
				if(CFG.HF[HFlight.Stop])
				{
					VSL.HorizontalSpeed.SetNeeded(Vector3d.zero);
					CFG.Nav.Off(); //any kind of navigation conflicts with the Stop program; obviously.
				}
				else if(CFG.HF[HFlight.NoseOnCourse])
					CFG.BR.OnIfNot(BearingMode.Auto);
				CFG.AT.OnIfNot(Attitude.Custom);
				goto case Multiplexer.Command.Resume;

			case Multiplexer.Command.Off:
				UnregisterFrom<SASBlocker>();
				ReleaseCPS();
				CFG.AT.OffIfOn(Attitude.Custom);
				CFG.BR.OffIfOn(BearingMode.Auto);
				EnableManualTranslation(false); 
				break;
			}
		}

		void EnableManualTranslation(bool enable = true)
		{
			VSL.Controls.ManualTranslationSwitch.Set(enable);
            if(!CFG.Enabled || VSL.Controls.ManualTranslationSwitch.On) return;
			var Changed = false;
			for(int i = 0, count = VSL.Engines.Active.Manual.Count; i < count; i++)
			{
				var e = VSL.Engines.Active.Manual[i];
				if(!e.engine.thrustPercentage.Equals(0))
				{
					e.limit = 0;
					e.forceThrustPercentage(0);
					Changed = true;
				}
			}
			if(Changed && TCA.ProfileSyncAllowed) 
				CFG.ActiveProfile.Update(VSL.Engines.Active);
		}

		protected override void OnAutopilotUpdate()
		{
			if(!IsActive) return;
			if(VSL.AutopilotDisabled) { output_filter.Reset(); return; }
			CFG.AT.OnIfNot(Attitude.Custom);
			//calculate prerequisites
            var thrust = VSL.Engines.DefThrust;
			needed_thrust_dir = -VSL.Physics.Up;
			if(CFG.HF[HFlight.Level])
            {
                thrust = VSL.Engines.CurrentDefThrustDir;
                VSL.Controls.ManualTranslationSwitch.Set(false);
            }
            else 
			{
				//set forward direction
				if(CFG.HF[HFlight.NoseOnCourse] && !VSL.HorizontalSpeed.NeededVector.IsZero())
					BRC.ForwardDirection = VSL.HorizontalSpeed.NeededVector;
				//calculate horizontal velocity
				CourseCorrection = Vector3d.zero;
				for(int i = 0, count = CourseCorrections.Count; i < count; i++)
					CourseCorrection += CourseCorrections[i];
				var nV  = VSL.HorizontalSpeed.NeededVector+CourseCorrection;
				var hV  = VSL.HorizontalSpeed.Vector-nV;
				var hVl = VSL.LocalDir(hV);
				var nVm = nV.magnitude;
				var hVm = hV.magnitude;
				var HVn = VSL.HorizontalSpeed.normalized;
				//if the manual translation can and should be used
				var rV  = hV; //velocity that is needed to be handled by attitude control of the total thrust
				var fV  = Vector3d.zero; //forward-backward velocity with respect to the manual thrust vector
				var with_manual_thrust = VSL.Engines.Active.Manual.Count > 0 && 
					(nVm >= HSC.TranslationUpperThreshold ||
					 hVm >= HSC.TranslationUpperThreshold ||
					 CourseCorrection.magnitude >= HSC.TranslationUpperThreshold);
				var manual_thrust = Vector3.ProjectOnPlane(VSL.Engines.DefManualThrust, VSL.Physics.Up);
				var zero_manual_thrust = manual_thrust.IsZero();
				if(with_manual_thrust &&
				   !zero_manual_thrust &&
				   Vector3.Dot(manual_thrust, hV) > 0)
				{
					thrust -= manual_thrust;
                    var axis = VSL.HorizontalSpeed.NeededVector.IsZero()?
                        manual_thrust : (Vector3)VSL.HorizontalSpeed.NeededVector;
                    rV = Vector3.ProjectOnPlane(hV, axis);
					fV = hV-rV;
				}
				var rVm = rV.magnitude;
				var fVm = Utils.ClampL(fV.magnitude, 1e-5);
				//calculate needed thrust direction
				if(!(with_manual_thrust && zero_manual_thrust &&
				     VSL.HorizontalSpeed.Absolute <= HSC.TranslationLowerThreshold) &&
				   rVm > HSC.RotationLowerThreshold && Utils.ClampL(rVm/fVm, 0) > HSC.RotationLowerThreshold)
				{
                    if(UseOldPid)
                    {
                        var GeeF  = Mathf.Sqrt(VSL.Physics.G/Utils.G0);
                        var MaxHv = Utils.ClampL(Vector3d.Project(VSL.vessel.acceleration, rV).magnitude*HSC.AccelerationFactor, HSC.MinHvThreshold);
                        var upF   = Utils.ClampL(Math.Pow(MaxHv/rVm, Utils.ClampL(HSC.HVCurve*GeeF, HSC.MinHVCurve)), GeeF) * Utils.ClampL(fVm/rVm, 1) / VSL.OnPlanetParams.TWRf;
                        needed_thrust_dir = rV.normalized - VSL.Physics.Up*upF;
                    }
                    else
                    {
                        //tune pid
                        var GeeF = Mathf.Sqrt(VSL.Physics.G/Utils.G0);
                        var fwdF = (float)Utils.ClampL(fVm/rVm, 1);
                        var turnTime = VSL.Torque.MaxCurrent.RotationTime2Phase(45, 
                                                                                Vector3.Cross(rV, VSL.Physics.Up).normalized,
                                                                                VSL.OnPlanetParams.GeeVSF);
                        needed_thrust_pid.setClamp(VSL.OnPlanetParams.TWRf/GeeF);
                        needed_thrust_pid.P = HSC.NeededThrustPID.P 
                            * (float)Utils.Clamp(2-nVm, 1, 2)
                            / turnTime 
                            / fwdF
                            / GeeF;
                        needed_thrust_pid.D = HSC.NeededThrustPID.D 
                            * Utils.ClampH(VSL.HorizontalSpeed.Absolute, 1)
                            * Mathf.Pow(turnTime, HSC.TurnTime_Curve) 
                            / fwdF;
                        //update OP and set needed thrust direction
                        var dV = Vector3d.Dot(rV, nV) < 0? -rVm : rVm;
                        needed_thrust_pid.Update((float)dV);
                        needed_thrust_dir = rV/dV*needed_thrust_pid.Action - VSL.Physics.Up;

                        TCAGui.AddDebugMessage("TWRf {}, TurnTime {}, fwdF {}\nNV PID: {}", 
                                               VSL.OnPlanetParams.TWRf, turnTime, fwdF, needed_thrust_pid);//debug
                    }
				}
                else
                    TCAGui.AddDebugMessage("TWRf {}\nNV PID: {}", 
                                           VSL.OnPlanetParams.TWRf, needed_thrust_pid);//debug
				//try to use translation controls (maneuver engines and RCS)
				if(hVm > HSC.TranslationLowerThreshold && TRA != null && CFG.CorrectWithTranslation)
				{
					var nVn = nVm > 0? nV/nVm : Vector3d.zero;
					var cV_lat = Vector3.ProjectOnPlane(CourseCorrection, nV);
					if(nVm < HSC.TranslationUpperThreshold || 
					   Mathf.Abs((float)Vector3d.Dot(HVn, nVn)) < HSC.TranslationMaxCos)
						TRA.AddDeltaV(hVl);
					else if(cV_lat.magnitude > HSC.TranslationLowerThreshold)
						TRA.AddDeltaV(-VSL.LocalDir(cV_lat));
				}
				//manual engine control
				if(with_manual_thrust)
				{
					//turn the nose if nesessary
					var pure_hV = VSL.HorizontalSpeed.Vector-VSL.HorizontalSpeed.NeededVector;
					var NVm = VSL.HorizontalSpeed.NeededVector.magnitude;
					var transF = 1f;
					if(pure_hV.magnitude >= HSC.RotationUpperThreshold &&
					   (NVm < HSC.TranslationLowerThreshold || 
					    Vector3.Dot(HVn, VSL.HorizontalSpeed.NeededVector/NVm) < HSC.RotationMaxCos))
					{
						var max_MT = VSL.Engines.ManualThrustLimits.MaxInPlane(VSL.Physics.UpL);
						if(!max_MT.IsZero())
						{
                            var axis = Vector3.ProjectOnPlane(VSL.OnPlanetParams.FwdL, VSL.Physics.UpL);
                            var angle = Utils.Angle2(max_MT, axis);
                            var rot = Quaternion.AngleAxis(angle, VSL.Physics.Up * Mathf.Sign(Vector3.Dot(max_MT, Vector3.right)));
							BRC.DirectionOverride = rot*pure_hV;
							transF = Utils.ClampL(Vector3.Dot(VSL.OnPlanetParams.Fwd, BRC.DirectionOverride.normalized), 0);
						}
					}
                    transF *= Utils.Clamp(1+Vector3.Dot(thrust.normalized, pure_hV.normalized)*HSC.ManualTranslation.ThrustF, 0, 1);
                    transF *= transF*transF*transF;
					translation_pid.I = (VSL.HorizontalSpeed > HSC.ManualTranslation.I_MinSpeed && 
					                     VSL.vessel.mainBody.atmosphere)? 
						HSC.ManualTranslation.PID.I*VSL.HorizontalSpeed : 0;
                    var D = VSL.Engines.ManualThrustSpeed.Project(hVl.normalized).magnitude;
                    if(D > 0) D = Mathf.Min(HSC.ManualTranslation.PID.D/D, HSC.ManualTranslation.D_Max);
                    translation_pid.D = D;
					translation_pid.Update((float)fVm);
					VSL.Controls.ManualTranslation = translation_pid.Action*hVl.CubeNorm()*transF;
					EnableManualTranslation(translation_pid.Action > 0);
				}
				else EnableManualTranslation(false);
				if(thrust.IsZero()) thrust = VSL.Engines.CurrentDefThrustDir;
				if(CFG.HF[HFlight.Stop]) VSL.Altitude.DontCorrectIfSlow();
			}
			needed_thrust_dir.Normalize();
			//tune filter
            output_filter.Tau = VSL.Torque.Slow ? 
				HSC.LowPassF / (1 + VSL.Torque.EnginesResponseTimeM * HSC.SlowTorqueF) : 
				HSC.LowPassF;
			ATC.SetCustomRotationW(thrust, output_filter.Update(needed_thrust_dir).normalized);

			#if DEBUG
//			LogF("\nthrust {}\nneeded {}\nfilterred {}\nAttitudeError {}", 
//			     thrust, needed_thrust_dir, filter.Value.normalized, VSL.Controls.AttitudeError);//debug
//			CSV(VSL.Physics.UT, 
//			    filter.Value.x, filter.Value.y, filter.Value.z,
//			    thrust.x, thrust.y, thrust.z);//debug
			#endif
		}
	}
}

