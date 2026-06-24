using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace ExamplePlugin
{
    public class ExamplePlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        public GameObject windowPrefab;

        private GameObject window;

        // Settings
        private string someValue1 = "";
        private float someValue2 = 5.0f;

        public void Awake()
        {
            // Register button to plugins window
            VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton("Example Plugin", this);

            // Create a window that will show when the button in plugins window is clicked
            window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab);

            // Load settings
            loadPluginSettings();

            // Hide the window by default
            if (window != null)
            {
                window.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 0);  
                window.SetActive(false);

                // Set ui component callbacks and loaded values
                window.GetComponentInChildren<Slider>()?.onValueChanged.AddListener((v) => { someValue2 = v; });
                window.GetComponentInChildren<Slider>()?.SetValueWithoutNotify(someValue2);

                window.GetComponentInChildren<InputField>()?.onValueChanged.AddListener((v) => { someValue1 = v; });
                window.GetComponentInChildren<InputField>()?.SetTextWithoutNotify(someValue1);

            }


        }

        /// <summary>
        /// Load plugin settings
        /// </summary>
        private void loadPluginSettings()
        {
            // Get settings in dictionary
            Dictionary<string, string> settings = VNyanInterface.VNyanInterface.VNyanSettings.loadSettings("ExamplePlugin.cfg");
            if (settings != null)
            {
                // Read string value
                settings.TryGetValue("SomeValue1", out someValue1);

                // Convert second value to decimal
                if (settings.TryGetValue("SomeValue2", out string s))
                {
                    float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out someValue2);
                }

            }
        }

        /// <summary>
        /// Called when VNyan is shutting down
        /// </summary>
        private void OnApplicationQuit()
        {
            // Save settings
            savePluginSettings();
        }

        private void savePluginSettings()
        {
            Dictionary<string, string> settings = new Dictionary<string, string>();
            settings["SomeValue1"] = someValue1;
            settings["SomeValue2"] = someValue2.ToString(CultureInfo.InvariantCulture); // Make sure to use InvariantCulture to avoid decimal delimeter errors

            VNyanInterface.VNyanInterface.VNyanSettings.saveSettings("ExamplePlugin.cfg", settings);
        }

        public void pluginButtonClicked()
        {
            // Flip the visibility of the window when plugin window button is clicked
            if (window != null)
            {
                window.SetActive(!window.activeSelf);
                if(window.activeSelf)
                    window.transform.SetAsLastSibling();
            }
                
        }

        public void Start()
        {

        }
    }
}
