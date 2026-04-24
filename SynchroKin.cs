using UnityEngine;
using UnityEngine.UI; 
using System.IO.Ports; 
using System; 
using System.Collections.Generic;
using TMPro;
using SFB; 

public class SynchroKin : MonoBehaviour
{
    // ==========================================
    // [1] APP NAVIGATION & MODES
    // ==========================================
    public enum AppMode { None, Recording, Practice, DataExtraction }
    public enum AppState { Dashboard, RoutineSelection, CalibrationPhase, PrepPhase, ActionPhase, Report } 
    
    [Header("UI State Machine")]
    public AppMode currentMode = AppMode.None;
    public AppState currentState = AppState.Dashboard;

    [Header("Canvas Panels")]
    public GameObject panelDashboard;
    public GameObject panelSelection; 
    public GameObject panelPractice;
    public GameObject panelReport;
    public GameObject backButton; 

    [Header("Data Extraction UI")]
    public GameObject btnExportCSV;
    public GameObject btnExportJSON;

    [Header("UI Text Elements")]
    public TextMeshProUGUI textInstructions;
    public TextMeshProUGUI textLiveScore;
    public TextMeshProUGUI textFinalFeedback;

    [Header("Camera Control")]
    public Transform mainCamera;
    public Transform viewStandard;  
    public Transform viewRecording; 
    public float cameraSpeed = 4f;

    [Header("Environment & Bots (Visibility)")]
    public GameObject liveAvatarModel;
    public GameObject ghostAvatarModel;
    public GameObject liveMat;   
    public GameObject ghostMat;  

    // ==========================================
    // [2] HARDWARE & CALIBRATION
    // ==========================================
    [Header("Deployment UI")]
    public TMP_InputField portInputField; // REPLACED: Dropdown is gone, simple text box is here

    [Header("Hardware Settings")]
    public string portName = "COM15"; 
    public int baudRate = 115200;
    public bool useDTR = true; 
    
    private SerialPort _serial; 
    private string _buffer = ""; 
    private bool _needsCalibration = false;

    [Header("Hardware Watchdog")]
    public float autoReconnectDelay = 5.0f; 
    private float _lastDataReceivedTime = 0f;

    [Header("Kinematic Smoothing & Filters")]
    public float maxAllowedJumpPerFrame = 50f; 
    public float liveSmoothSpeed = 15f; 
    
    private int _chestGlitchFrames = 0;
    private int _bicepGlitchFrames = 0;
    private int _forearmGlitchFrames = 0;
    
    [Header("Constraints")]
    public bool calibrateArmDown = true;
    public Vector3 armDownOffset = new Vector3(80f, 0f, 0f); 
    public float minElbowBend = -5f; 
    public float maxElbowBend = 150f; 

    // ==========================================
    // [3] AVATAR & KINEMATICS
    // ==========================================
    [Header("Skeletal Targets")]
    public Transform liveChestBone; 
    public Transform ghostChestBone; 
    public Transform liveBicepBone; 
    public Transform ghostBicepBone; 
    public Transform liveForearmBone; 
    public Transform ghostForearmBone;
    public SkinnedMeshRenderer avatarSkin;
    
    [Header("Ghost Visual Hacks")]
    public Vector3 ghostForearmFlip = new Vector3(0f, 180f, 0f); 

    private Quaternion _targetChestLiveRot = Quaternion.identity; 
    private Quaternion _chestCalibInverse = Quaternion.identity; 

    private Quaternion _targetBicepLiveRot = Quaternion.identity; 
    private Quaternion _bicepCalibInverse = Quaternion.identity; 
    private Quaternion _targetForearmLiveRot = Quaternion.identity; 
    private Quaternion _forearmCalibInverse = Quaternion.identity; 

    // ==========================================
    // [4] ROUTINE & MEMORY ENGINE
    // ==========================================
    [System.Serializable]
    public class ExercisePose {
        public string poseName;
        public float fallbackBicepX, fallbackBicepY, fallbackBicepZ; 
        public float fallbackForearmX, fallbackForearmY, fallbackForearmZ; 
        
        public Quaternion recordedChest = Quaternion.identity; 
        public Quaternion recordedBicep = Quaternion.identity;
        public Quaternion recordedForearm = Quaternion.identity;
        public bool isRecorded = false; 
    }

    public class PatientDataPoint {
        public string stepName;
        public Quaternion chestRot;
        public Quaternion bicepRot;
        public Quaternion forearmRot;
    }

    private ExercisePose[] _tadasanaRoutine;
    private ExercisePose[] _backScratchRoutine;
    private ExercisePose[] _activeRoutine; 
    private string _activeRoutineName = ""; 
    
    private int _currentPoseIndex = 0; 
    private float _phaseTimer = 5f; 
    
    private List<float> _stepAccuracies = new List<float>(); 
    private List<PatientDataPoint> _patientDataLog = new List<PatientDataPoint>(); 

    private float _currentStepErrorSum = 0f;
    private int _currentStepFrameCount = 0;

    void Start()
    {
        _tadasanaRoutine = new ExercisePose[] {
            new ExercisePose { poseName = "Step 1: Arm Down", fallbackBicepX = 80f, fallbackBicepY = 0f, fallbackBicepZ = 0f, fallbackForearmX = 0f, fallbackForearmY = 0f, fallbackForearmZ = 0f },
            new ExercisePose { poseName = "Step 2: T-Pose", fallbackBicepX = -9.779f, fallbackBicepY = 94.98f, fallbackBicepZ = -2.746f, fallbackForearmX = -357.96f, fallbackForearmY = -268.754f, fallbackForearmZ = 353.518f },
            new ExercisePose { poseName = "Step 3: High Reach", fallbackBicepX = -6.389f, fallbackBicepY = 89.839f, fallbackBicepZ = -73.501f, fallbackForearmX = -376.552f, fallbackForearmY = -458.39f, fallbackForearmZ = 367.583f }
        };

        _backScratchRoutine = new ExercisePose[] {
            new ExercisePose { poseName = "Step 1: Resting Position", fallbackBicepX = 80f, fallbackBicepY = 0f, fallbackBicepZ = 0f, fallbackForearmX = 0f, fallbackForearmY = 0f, fallbackForearmZ = 0f },
            new ExercisePose { poseName = "Step 2: Overhead Reach", fallbackBicepX = -76.821f, fallbackBicepY = -61.347f, fallbackBicepZ = 61.986f, fallbackForearmX = -363.099f, fallbackForearmY = -91.508f, fallbackForearmZ = 367.239f },
            new ExercisePose { poseName = "Step 3: Back Scratch Drop", fallbackBicepX = -76.821f, fallbackBicepY = -61.347f, fallbackBicepZ = 61.986f, fallbackForearmX = -326.665f, fallbackForearmY = -81.472f, fallbackForearmZ = 154.431f }
        };

        LoadCalibration(); 
        LoadRecordedRoutines(); 

        // Set the text box to whatever the default portName is
        if (portInputField != null) {
            portInputField.text = portName;
        }

        OpenConnection();
        GoToDashboard(); 
    }

    // NEW: Simple text box submission
    public void OnPortInputSubmitted(string userInput)
    {
        if (string.IsNullOrEmpty(userInput)) return;
        
        // This makes it foolproof. If they type "  com5  ", it forces it to "COM5"
        portName = userInput.ToUpper().Trim(); 
        OpenConnection(); 
    }

    public void StartRecordingMode() { currentMode = AppMode.Recording; ShowSelectionScreen(); }
    public void StartPracticeMode() { currentMode = AppMode.Practice; ShowSelectionScreen(); }
    public void StartDataExtractionMode() { currentMode = AppMode.DataExtraction; ShowSelectionScreen(); } 

    private void ShowSelectionScreen()
    {
        currentState = AppState.RoutineSelection;
        UpdateUIPanels();
    }

    public void SelectTadasana() { _activeRoutine = _tadasanaRoutine; _activeRoutineName = "Tadasana"; StartRoutine(); }
    public void SelectBackScratch() { _activeRoutine = _backScratchRoutine; _activeRoutineName = "Back Scratch Test"; StartRoutine(); }

    private void StartRoutine()
    {
        _currentPoseIndex = 0;
        _phaseTimer = 3f; 
        _stepAccuracies.Clear();
        _patientDataLog.Clear(); 
        
        if (currentMode == AppMode.Recording) {
            foreach(var pose in _activeRoutine) pose.isRecorded = false;
        }

        ResetAvatarsToDefault();
        currentState = AppState.CalibrationPhase; 
        UpdateUIPanels();
    }

    private void ResetAvatarsToDefault()
    {
        _targetChestLiveRot = Quaternion.identity; 
        _targetBicepLiveRot = Quaternion.identity; 
        _targetForearmLiveRot = Quaternion.identity; 

        if (liveChestBone != null) liveChestBone.localRotation = Quaternion.identity;
        if (liveBicepBone != null) liveBicepBone.localRotation = Quaternion.identity;
        if (liveForearmBone != null) liveForearmBone.localRotation = Quaternion.identity;

        if (ghostChestBone != null) ghostChestBone.localRotation = Quaternion.identity;
        if (ghostBicepBone != null) ghostBicepBone.localRotation = Quaternion.identity;
        if (ghostForearmBone != null) ghostForearmBone.localRotation = Quaternion.identity;

        if (avatarSkin != null) avatarSkin.material.color =new Color(0.5f, 0.8f, 1f);
    }

    public void GoToDashboard() 
    { 
        currentMode = AppMode.None; 
        currentState = AppState.Dashboard; 
        ResetAvatarsToDefault(); 
        UpdateUIPanels(); 
    }

    private void UpdateUIPanels()
    {
        panelDashboard.SetActive(currentState == AppState.Dashboard);
        panelSelection.SetActive(currentState == AppState.RoutineSelection); 
        panelPractice.SetActive(currentState == AppState.CalibrationPhase || currentState == AppState.PrepPhase || currentState == AppState.ActionPhase);
        panelReport.SetActive(currentState == AppState.Report);

        if (backButton != null) backButton.SetActive(currentState != AppState.Dashboard);

        bool showExportButtons = (currentState == AppState.Report && currentMode == AppMode.DataExtraction);
        if (btnExportCSV != null) btnExportCSV.SetActive(showExportButtons);
        if (btnExportJSON != null) btnExportJSON.SetActive(showExportButtons);

        bool showBots = (currentState != AppState.Dashboard);
        bool isRecordingOrExtraction = (currentMode == AppMode.Recording || currentMode == AppMode.DataExtraction);
        
        if (liveAvatarModel != null) liveAvatarModel.SetActive(showBots);
        if (liveMat != null) liveMat.SetActive(showBots);
        
        bool showGhost = showBots && !isRecordingOrExtraction;
        if (ghostAvatarModel != null) ghostAvatarModel.SetActive(showGhost);
        if (ghostMat != null) ghostMat.SetActive(showGhost);

        if (avatarSkin != null) avatarSkin.enabled = showBots; 
    }

    void OpenConnection()
    {
        try {
            if (_serial != null && _serial.IsOpen) _serial.Close();
            _serial = new SerialPort(portName, baudRate);
            _serial.ReadTimeout = 10; 
            
            _serial.DtrEnable = useDTR;  
            _serial.RtsEnable = useDTR; 
            
            _serial.Open();
            _serial.DiscardInBuffer(); 
            _lastDataReceivedTime = Time.time; 
            
            Debug.Log("<color=cyan>SUCCESS: Port " + portName + " successfully opened!</color>");
            
        } catch (Exception e) { 
            Debug.LogError("<color=red>FAILED TO OPEN PORT:</color> " + e.Message);
        }
    }

    void LateUpdate()
    {
        if (Time.time - _lastDataReceivedTime > autoReconnectDelay)
        {
            Debug.LogWarning("Watchdog Triggered: No data received for " + autoReconnectDelay + " seconds. Rebooting port...");
            OpenConnection();
            _lastDataReceivedTime = Time.time; 
        }

        if (UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame) _needsCalibration = true;

        HandleHardwareInput();
        HandleCameraMovement(); 

        Quaternion finalLiveChest = _targetChestLiveRot;
        Quaternion finalLiveBicep = _targetBicepLiveRot;
        Quaternion finalLiveForearm = _targetForearmLiveRot;

        if (currentState == AppState.CalibrationPhase)
        {
            finalLiveChest = Quaternion.identity;
            finalLiveBicep = calibrateArmDown ? Quaternion.Euler(armDownOffset) : Quaternion.identity;
            finalLiveForearm = Quaternion.identity;
        }

        if (liveChestBone != null) liveChestBone.localRotation = Quaternion.Slerp(liveChestBone.localRotation, finalLiveChest, Time.deltaTime * liveSmoothSpeed);
        if (liveBicepBone != null) liveBicepBone.localRotation = Quaternion.Slerp(liveBicepBone.localRotation, finalLiveBicep, Time.deltaTime * liveSmoothSpeed);
        if (liveForearmBone != null) liveForearmBone.localRotation = Quaternion.Slerp(liveForearmBone.localRotation, finalLiveForearm, Time.deltaTime * liveSmoothSpeed);

        if (currentState == AppState.CalibrationPhase || currentState == AppState.PrepPhase || currentState == AppState.ActionPhase)
            UpdateStateTimerAndScoring();
    }

    private void HandleCameraMovement()
    {
        if (mainCamera == null || viewStandard == null || viewRecording == null) return;
        Transform targetView = (currentMode == AppMode.Recording || currentMode == AppMode.DataExtraction) ? viewRecording : viewStandard;
        if (currentState != AppState.Dashboard)
        {
            mainCamera.position = Vector3.Lerp(mainCamera.position, targetView.position, Time.deltaTime * cameraSpeed);
            mainCamera.rotation = Quaternion.Slerp(mainCamera.rotation, targetView.rotation, Time.deltaTime * cameraSpeed);
        }
    }

    private void HandleHardwareInput()
    {
        if (_serial == null || !_serial.IsOpen) return;
        if (_serial.BytesToRead > 0)
        {
            try
            {
                string incoming = _serial.ReadExisting();
                _buffer += incoming;
                if (_buffer.Contains("\n"))
                {
                    _lastDataReceivedTime = Time.time;

                    string[] lines = _buffer.Split('\n');
                    _buffer = lines[lines.Length - 1]; 
                    string latestLine = lines[lines.Length - 2].Trim();
                    if (string.IsNullOrEmpty(latestLine)) return;

                    if (latestLine.Contains("C_W:") && latestLine.Contains("B_W:") && latestLine.Contains("F_W:"))
                    {
                        string[] parts = latestLine.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                        
                        if (parts.Length >= 12) 
                        {
                            float cw = float.Parse(parts[0].Split(':')[1]); float cx = float.Parse(parts[1].Split(':')[1]); float cy = float.Parse(parts[2].Split(':')[1]); float cz = float.Parse(parts[3].Split(':')[1]); 
                            float bw = float.Parse(parts[4].Split(':')[1]); float bx = float.Parse(parts[5].Split(':')[1]); float by = float.Parse(parts[6].Split(':')[1]); float bz = float.Parse(parts[7].Split(':')[1]); 
                            float fw = float.Parse(parts[8].Split(':')[1]); float fx = float.Parse(parts[9].Split(':')[1]); float fy = float.Parse(parts[10].Split(':')[1]); float fz = float.Parse(parts[11].Split(':')[1]); 

                            Quaternion chestMapped = new Quaternion(cx, cy, cz, cw); 
                            Quaternion bicepMapped = new Quaternion(-bx, by, -bz, bw); 
                            Quaternion forearmMapped = new Quaternion(fy, -fx, fz, fw); 

                            if (_needsCalibration) 
                            { 
                                Quaternion offset = calibrateArmDown ? Quaternion.Euler(armDownOffset) : Quaternion.identity;
                                _chestCalibInverse = Quaternion.Inverse(chestMapped); 
                                _bicepCalibInverse = offset * Quaternion.Inverse(bicepMapped); 
                                _forearmCalibInverse = offset * Quaternion.Inverse(forearmMapped);
                                _needsCalibration = false; 
                                SaveCalibration(); 
                            }

                            Quaternion absoluteChestRaw = _chestCalibInverse * chestMapped;
                            Quaternion absoluteBicepRaw = _bicepCalibInverse * bicepMapped;
                            Quaternion absoluteForearmRaw = _forearmCalibInverse * forearmMapped;

                            float chestJump = Quaternion.Angle(_targetChestLiveRot, absoluteChestRaw);
                            if (chestJump > maxAllowedJumpPerFrame && _chestGlitchFrames < 5) { _chestGlitchFrames++; } else { _targetChestLiveRot = absoluteChestRaw; _chestGlitchFrames = 0; }

                            Quaternion localBicepRaw = Quaternion.Inverse(_targetChestLiveRot) * absoluteBicepRaw;
                            float bicepJump = Quaternion.Angle(_targetBicepLiveRot, localBicepRaw);
                            if (bicepJump > maxAllowedJumpPerFrame && _bicepGlitchFrames < 5) { _bicepGlitchFrames++; } else { _targetBicepLiveRot = localBicepRaw; _bicepGlitchFrames = 0; }

                            Quaternion localForearmRaw = Quaternion.Inverse(absoluteBicepRaw) * absoluteForearmRaw;
                            Quaternion constrainedLocalForearm = ApplyElbowConstraint(localForearmRaw);
                            float forearmJump = Quaternion.Angle(_targetForearmLiveRot, constrainedLocalForearm);
                            if (forearmJump > maxAllowedJumpPerFrame && _forearmGlitchFrames < 5) { _forearmGlitchFrames++; } else { _targetForearmLiveRot = constrainedLocalForearm; _forearmGlitchFrames = 0; }
                        }
                    }
                }
            }
            catch (Exception) { } 
        }
    }

    private void SaveCalibration()
    {
        PlayerPrefs.SetFloat("chestInvX", _chestCalibInverse.x); PlayerPrefs.SetFloat("chestInvY", _chestCalibInverse.y); PlayerPrefs.SetFloat("chestInvZ", _chestCalibInverse.z); PlayerPrefs.SetFloat("chestInvW", _chestCalibInverse.w);
        PlayerPrefs.SetFloat("bicepInvX", _bicepCalibInverse.x); PlayerPrefs.SetFloat("bicepInvY", _bicepCalibInverse.y); PlayerPrefs.SetFloat("bicepInvZ", _bicepCalibInverse.z); PlayerPrefs.SetFloat("bicepInvW", _bicepCalibInverse.w);
        PlayerPrefs.SetFloat("forearmInvX", _forearmCalibInverse.x); PlayerPrefs.SetFloat("forearmInvY", _forearmCalibInverse.y); PlayerPrefs.SetFloat("forearmInvZ", _forearmCalibInverse.z); PlayerPrefs.SetFloat("forearmInvW", _forearmCalibInverse.w);
        PlayerPrefs.Save();
    }

    private void LoadCalibration()
    {
        if (PlayerPrefs.HasKey("chestInvX"))
        {
            _chestCalibInverse = new Quaternion(PlayerPrefs.GetFloat("chestInvX"), PlayerPrefs.GetFloat("chestInvY"), PlayerPrefs.GetFloat("chestInvZ"), PlayerPrefs.GetFloat("chestInvW"));
            _bicepCalibInverse = new Quaternion(PlayerPrefs.GetFloat("bicepInvX"), PlayerPrefs.GetFloat("bicepInvY"), PlayerPrefs.GetFloat("bicepInvZ"), PlayerPrefs.GetFloat("bicepInvW"));
            _forearmCalibInverse = new Quaternion(PlayerPrefs.GetFloat("forearmInvX"), PlayerPrefs.GetFloat("forearmInvY"), PlayerPrefs.GetFloat("forearmInvZ"), PlayerPrefs.GetFloat("forearmInvW"));
        }
    }

    private void SavePoseToMemory(string routineName, int index, ExercisePose pose)
    {
        string prefix = routineName + "_" + index + "_";
        PlayerPrefs.SetFloat(prefix + "cX", pose.recordedChest.x); PlayerPrefs.SetFloat(prefix + "cY", pose.recordedChest.y); PlayerPrefs.SetFloat(prefix + "cZ", pose.recordedChest.z); PlayerPrefs.SetFloat(prefix + "cW", pose.recordedChest.w);
        PlayerPrefs.SetFloat(prefix + "bX", pose.recordedBicep.x); PlayerPrefs.SetFloat(prefix + "bY", pose.recordedBicep.y); PlayerPrefs.SetFloat(prefix + "bZ", pose.recordedBicep.z); PlayerPrefs.SetFloat(prefix + "bW", pose.recordedBicep.w);
        PlayerPrefs.SetFloat(prefix + "fX", pose.recordedForearm.x); PlayerPrefs.SetFloat(prefix + "fY", pose.recordedForearm.y); PlayerPrefs.SetFloat(prefix + "fZ", pose.recordedForearm.z); PlayerPrefs.SetFloat(prefix + "fW", pose.recordedForearm.w);
        PlayerPrefs.SetInt(prefix + "saved", 1);
        PlayerPrefs.Save();
    }

    private void LoadRecordedRoutines()
    {
        LoadRoutineData("Tadasana", _tadasanaRoutine);
        LoadRoutineData("Back Scratch Test", _backScratchRoutine);
    }

    private void LoadRoutineData(string routineName, ExercisePose[] routine)
    {
        for (int i = 0; i < routine.Length; i++)
        {
            string prefix = routineName + "_" + i + "_";
            if (PlayerPrefs.GetInt(prefix + "saved", 0) == 1)
            {
                routine[i].recordedChest = new Quaternion(PlayerPrefs.GetFloat(prefix + "cX"), PlayerPrefs.GetFloat(prefix + "cY"), PlayerPrefs.GetFloat(prefix + "cZ"), PlayerPrefs.GetFloat(prefix + "cW"));
                routine[i].recordedBicep = new Quaternion(PlayerPrefs.GetFloat(prefix + "bX"), PlayerPrefs.GetFloat(prefix + "bY"), PlayerPrefs.GetFloat(prefix + "bZ"), PlayerPrefs.GetFloat(prefix + "bW"));
                routine[i].recordedForearm = new Quaternion(PlayerPrefs.GetFloat(prefix + "fX"), PlayerPrefs.GetFloat(prefix + "fY"), PlayerPrefs.GetFloat(prefix + "fZ"), PlayerPrefs.GetFloat(prefix + "fW"));
                routine[i].isRecorded = true;
            }
        }
    }

    private Quaternion ApplyElbowConstraint(Quaternion localForearmRotation)
    {
        Vector3 euler = localForearmRotation.eulerAngles;
        float pitch = NormalizeAngle(euler.x); float roll = NormalizeAngle(euler.y); float yaw = NormalizeAngle(euler.z);
        yaw = Mathf.Clamp(yaw, minElbowBend, maxElbowBend);
        return Quaternion.Euler(pitch, roll, yaw);
    }

    private float NormalizeAngle(float angle)
    {
        angle = angle % 360f;
        if (angle > 180f) return angle - 360f;
        if (angle < -180f) return angle + 360f;
        return angle;
    }

    private void UpdateStateTimerAndScoring()
    {
        if (ghostBicepBone == null || liveBicepBone == null || _activeRoutine == null) return;
        ExercisePose currentPose = _activeRoutine[_currentPoseIndex];

        if (currentMode == AppMode.Recording)
        {
            if (ghostChestBone != null) ghostChestBone.localRotation = Quaternion.Slerp(ghostChestBone.localRotation, liveChestBone.localRotation, Time.deltaTime * 5f);
            ghostBicepBone.localRotation = Quaternion.Slerp(ghostBicepBone.localRotation, liveBicepBone.localRotation, Time.deltaTime * 5f);
            if (ghostForearmBone != null) ghostForearmBone.localRotation = Quaternion.Slerp(ghostForearmBone.localRotation, liveForearmBone.localRotation * Quaternion.Euler(ghostForearmFlip), Time.deltaTime * 5f);
        }
        else if (currentMode == AppMode.Practice)
        {
            Quaternion targetChest = Quaternion.identity;
            Quaternion targetBicep;
            Quaternion targetForearm;

            if (currentPose.isRecorded)
            {
                targetChest = currentPose.recordedChest;
                targetBicep = currentPose.recordedBicep;
                targetForearm = currentPose.recordedForearm;
            }
            else
            {
                targetBicep = Quaternion.Euler(currentPose.fallbackBicepX, currentPose.fallbackBicepY, currentPose.fallbackBicepZ);
                targetForearm = Quaternion.Euler(currentPose.fallbackForearmX, currentPose.fallbackForearmY, currentPose.fallbackForearmZ);
            }

            if (ghostChestBone != null) ghostChestBone.localRotation = Quaternion.Slerp(ghostChestBone.localRotation, targetChest, Time.deltaTime * 6f);
            ghostBicepBone.localRotation = Quaternion.Slerp(ghostBicepBone.localRotation, targetBicep, Time.deltaTime * 6f);
            if (ghostForearmBone != null) ghostForearmBone.localRotation = Quaternion.Slerp(ghostForearmBone.localRotation, targetForearm * Quaternion.Euler(ghostForearmFlip), Time.deltaTime * 6f);
        }

        _phaseTimer -= Time.deltaTime;

        if (currentState == AppState.CalibrationPhase)
        {
            textInstructions.text = $"<size=120%>Auto-Calibration</size>";
            textLiveScore.text = $"<color=orange>STAND STILL</color>\nResting position: {Mathf.CeilToInt(_phaseTimer)}s";
            
            if (avatarSkin != null) avatarSkin.material.color = new Color(1f, 0.6f, 0f); 

            if (_phaseTimer <= 0)
            {
                _needsCalibration = true; 
                currentState = AppState.PrepPhase;
                _phaseTimer = 5f; 
                _currentStepErrorSum = 0f;
                _currentStepFrameCount = 0;
            }
        }
        else if (currentState == AppState.PrepPhase)
        {
            textInstructions.text = $"<size=150%>{currentPose.poseName}</size>";
            textLiveScore.text = $"<color=yellow>PREPARE</color>\nStarting in: {Mathf.CeilToInt(_phaseTimer)}s";
            if (avatarSkin != null) avatarSkin.material.color = new Color(0.5f, 0.8f, 1f); 

            if (_phaseTimer <= 0)
            {
                currentState = AppState.ActionPhase;
                _phaseTimer = 5f; 
                _currentStepErrorSum = 0f;
                _currentStepFrameCount = 0;
            }
        }
        else if (currentState == AppState.ActionPhase)
        {
            if (currentMode == AppMode.Recording)
            {
                textLiveScore.text = $"<color=red>RECORDING BASELINE</color>\nHold steady: {Mathf.CeilToInt(_phaseTimer)}s";
                if (avatarSkin != null) avatarSkin.material.color = Color.red;

                if (_phaseTimer <= 0)
                {
                    currentPose.recordedChest = _targetChestLiveRot;
                    currentPose.recordedBicep = _targetBicepLiveRot;
                    currentPose.recordedForearm = _targetForearmLiveRot;
                    currentPose.isRecorded = true;

                    SavePoseToMemory(_activeRoutineName, _currentPoseIndex, currentPose);
                    AdvanceToNextStep();
                }
            }
            else if (currentMode == AppMode.DataExtraction)
            {
                textLiveScore.text = $"<color=#A020F0>ASSESSMENT ACTIVE</color>\nHold posture: {Mathf.CeilToInt(_phaseTimer)}s";
                if (avatarSkin != null) avatarSkin.material.color = new Color(0.8f, 0.4f, 1f); 

                if (_phaseTimer <= 0)
                {
                    PatientDataPoint dp = new PatientDataPoint();
                    dp.stepName = currentPose.poseName;
                    dp.chestRot = _targetChestLiveRot;
                    dp.bicepRot = _targetBicepLiveRot;
                    dp.forearmRot = _targetForearmLiveRot;
                    _patientDataLog.Add(dp);

                    AdvanceToNextStep();
                }
            }
            else if (currentMode == AppMode.Practice)
            {
                textLiveScore.text = $"<color=green>PRACTICE</color>\nTime Left: {Mathf.CeilToInt(_phaseTimer)}s";
                if (avatarSkin != null) avatarSkin.material.color = Color.green;

                Quaternion trueTargetBicep = currentPose.isRecorded ? currentPose.recordedBicep : Quaternion.Euler(currentPose.fallbackBicepX, currentPose.fallbackBicepY, currentPose.fallbackBicepZ);
                Quaternion trueTargetForearm = currentPose.isRecorded ? currentPose.recordedForearm : Quaternion.Euler(currentPose.fallbackForearmX, currentPose.fallbackForearmY, currentPose.fallbackForearmZ);

                float bicepError = Quaternion.Angle(liveBicepBone.localRotation, trueTargetBicep);
                float forearmError = 0f;
                if (liveForearmBone != null && ghostForearmBone != null) {
                    forearmError = Quaternion.Angle(liveForearmBone.localRotation, trueTargetForearm);
                }
                
                _currentStepErrorSum += (bicepError + forearmError) / 2f; 
                _currentStepFrameCount++;

                if (_phaseTimer <= 0)
                {
                    if (_currentStepFrameCount > 0)
                    {
                        _stepAccuracies.Add(_currentStepErrorSum / _currentStepFrameCount);
                    }
                    AdvanceToNextStep();
                }
            }
        }
    }

    private void AdvanceToNextStep()
    {
        _currentPoseIndex++; 
        if (_currentPoseIndex >= _activeRoutine.Length)
        {
            if (currentMode == AppMode.Recording) { ShowRecordingSuccess(); }
            else if (currentMode == AppMode.DataExtraction) { ShowExtractionComplete(); } 
            else { GenerateReportCard(); }
        }
        else
        {
            currentState = AppState.PrepPhase; 
            _phaseTimer = 5f;
        }
    }

    private void ShowRecordingSuccess()
    {
        currentState = AppState.Report;
        UpdateUIPanels();

        if (textFinalFeedback != null) 
        {
            textFinalFeedback.text = $"<b><size=120%><color=#42bcf5>CALIBRATION COMPLETE</color></size></b>\n\n" +
                                     $"<color=green>SUCCESS:</color> <b>{_activeRoutineName}</b> baseline has been recorded.\n\n" +
                                     $"Your kinematic targets are now saved into memory.\n\n" +
                                     $"You may now click 'Return to Dashboard' to enter Practice Mode.";
        }
    }

    private void ShowExtractionComplete()
    {
        currentState = AppState.Report;
        UpdateUIPanels();

        if (textFinalFeedback != null) 
        {
            textFinalFeedback.text = $"<b><size=120%><color=#A020F0>DATA EXTRACTION COMPLETE</color></size></b>\n\n" +
                                     $"<color=green>SUCCESS:</color> Patient mobility data successfully captured.\n\n" +
                                     $"Please select an export format below to save the data to your hard drive for AI processing.";
        }
    }

    private void GenerateReportCard()
    {
        currentState = AppState.Report;
        UpdateUIPanels();
        float totalAvg = 0f;
        
        string report = $"<b><size=120%>CLINICAL KINEMATIC REPORT: {_activeRoutineName}</size></b>\n\n";
        
        for (int i = 0; i < _stepAccuracies.Count; i++) { 
            report += $"Step {i + 1} Deviation: {_stepAccuracies[i]:F1}°\n"; 
            totalAvg += _stepAccuracies[i]; 
        }
        
        if (_stepAccuracies.Count > 0) {
            totalAvg /= _stepAccuracies.Count;
            report += $"\n<b><color=yellow>Overall System Accuracy: {totalAvg:F1}°</color></b>\n\n";
            report += "<b><color=#42bcf5>BIOMECHANICS ANALYSIS:</color></b>\n";
            
            bool detectedIssues = false;
            
            if (_activeRoutineName == "Tadasana")
            {
                if (_stepAccuracies.Count > 1 && _stepAccuracies[1] > 18f) { detectedIssues = true; report += "<color=#ffaa00>► Step 2 (Lateral T-Pose):</color> High deviation here indicates restricted <i>Shoulder Abduction</i>.\n\n"; }
                if (_stepAccuracies.Count > 2 && _stepAccuracies[2] > 18f) { detectedIssues = true; report += "<color=#ffaa00>► Step 3 (High Reach):</color> High deviation indicates restricted <i>Overhead Flexion</i>.\n\n"; }
            }
            else if (_activeRoutineName == "Back Scratch Test")
            {
                if (_stepAccuracies.Count > 1 && _stepAccuracies[1] > 18f) { detectedIssues = true; report += "<color=#ffaa00>► Step 2 (Overhead Reach):</color> High deviation here indicates restricted <i>Shoulder Flexion / External Rotation</i>.\n\n"; }
                if (_stepAccuracies.Count > 2 && _stepAccuracies[2] > 18f) { detectedIssues = true; report += "<color=#ffaa00>► Step 3 (Back Scratch Drop):</color> High deviation indicates restricted <i>Elbow Flexion</i>.\n\n"; }
            }
            
            if (!detectedIssues) { report += "<color=green>► Full Range of Motion verified. No severe kinematic restrictions detected. Excellent mobility!</color>"; }
        }
        if (textFinalFeedback != null) textFinalFeedback.text = report;
    }
    
    public void ExportPatientDataCSV()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string defaultFileName = $"PatientData_{_activeRoutineName.Replace(" ", "")}_{timestamp}";

        string path = StandaloneFileBrowser.SaveFilePanel("Save Patient Data CSV", "", defaultFileName, "csv");

        if (string.IsNullOrEmpty(path)) return; 

        string csvContent = "Routine,Step,Chest_Qx,Chest_Qy,Chest_Qz,Chest_Qw,Chest_Ex,Chest_Ey,Chest_Ez,Bicep_Qx,Bicep_Qy,Bicep_Qz,Bicep_Qw,Bicep_Ex,Bicep_Ey,Bicep_Ez,Forearm_Qx,Forearm_Qy,Forearm_Qz,Forearm_Qw,Forearm_Ex,Forearm_Ey,Forearm_Ez\n";

        foreach(var dp in _patientDataLog)
        {
            Vector3 cE = dp.chestRot.eulerAngles; Vector3 bE = dp.bicepRot.eulerAngles; Vector3 fE = dp.forearmRot.eulerAngles;
            csvContent += $"{_activeRoutineName},{dp.stepName}," +
                          $"{dp.chestRot.x:F4},{dp.chestRot.y:F4},{dp.chestRot.z:F4},{dp.chestRot.w:F4},{cE.x:F2},{cE.y:F2},{cE.z:F2}," +
                          $"{dp.bicepRot.x:F4},{dp.bicepRot.y:F4},{dp.bicepRot.z:F4},{dp.bicepRot.w:F4},{bE.x:F2},{bE.y:F2},{bE.z:F2}," +
                          $"{dp.forearmRot.x:F4},{dp.forearmRot.y:F4},{dp.forearmRot.z:F4},{dp.forearmRot.w:F4},{fE.x:F2},{fE.y:F2},{fE.z:F2}\n";
        }

        try 
        {
            System.IO.File.WriteAllText(path, csvContent);
            if (textFinalFeedback != null) textFinalFeedback.text += $"\n\n<color=green>SAVED CSV TO:</color>\n<size=60%><i>{path}</i></size>";
        } 
        catch (Exception e) { if (textFinalFeedback != null) textFinalFeedback.text += $"\n\n<color=red>ERROR SAVING CSV:</color>\n{e.Message}"; }
    }

    public void ExportPatientDataJSON()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string defaultFileName = $"PatientData_{_activeRoutineName.Replace(" ", "")}_{timestamp}";

        string path = StandaloneFileBrowser.SaveFilePanel("Save Patient Data JSON", "", defaultFileName, "json");

        if (string.IsNullOrEmpty(path)) return; 

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"routine\": \"{_activeRoutineName}\",");
        sb.AppendLine($"  \"timestamp\": \"{timestamp}\",");
        sb.AppendLine("  \"frames\": [");

        for (int i = 0; i < _patientDataLog.Count; i++)
        {
            var dp = _patientDataLog[i];
            Vector3 cE = dp.chestRot.eulerAngles; Vector3 bE = dp.bicepRot.eulerAngles; Vector3 fE = dp.forearmRot.eulerAngles;

            sb.AppendLine("    {");
            sb.AppendLine($"      \"stepName\": \"{dp.stepName}\",");
            sb.AppendLine($"      \"chest\": {{ \"q\": [{dp.chestRot.x:F4}, {dp.chestRot.y:F4}, {dp.chestRot.z:F4}, {dp.chestRot.w:F4}], \"e\": [{cE.x:F2}, {cE.y:F2}, {cE.z:F2}] }},");
            sb.AppendLine($"      \"bicep\": {{ \"q\": [{dp.bicepRot.x:F4}, {dp.bicepRot.y:F4}, {dp.bicepRot.z:F4}, {dp.bicepRot.w:F4}], \"e\": [{bE.x:F2}, {bE.y:F2}, {bE.z:F2}] }},");
            sb.AppendLine($"      \"forearm\": {{ \"q\": [{dp.forearmRot.x:F4}, {dp.forearmRot.y:F4}, {dp.forearmRot.z:F4}, {dp.forearmRot.w:F4}], \"e\": [{fE.x:F2}, {fE.y:F2}, {fE.z:F2}] }}");
            sb.Append("    }");
            if (i < _patientDataLog.Count - 1) sb.AppendLine(","); else sb.AppendLine();
        }
        sb.AppendLine("\n  ]");
        sb.AppendLine("}");

        try 
        {
            System.IO.File.WriteAllText(path, sb.ToString());
            if (textFinalFeedback != null) textFinalFeedback.text += $"\n\n<color=green>SAVED JSON TO:</color>\n<size=60%><i>{path}</i></size>";
        } 
        catch (Exception e) { if (textFinalFeedback != null) textFinalFeedback.text += $"\n\n<color=red>ERROR SAVING JSON:</color>\n{e.Message}"; }
    }

    void OnDisable() { if (_serial != null && _serial.IsOpen) _serial.Close(); }
    void OnApplicationQuit() { if (_serial != null && _serial.IsOpen) _serial.Close(); }
}