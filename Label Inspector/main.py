#!/usr/bin/env python3
"""
Label Inspector - Advanced OCR and Label Analysis Tool
Based on RealTime-OCR by Nathan Aday
"""

import sys
import os
from PyQt5.QtWidgets import QApplication
from gui.main_window import MainWindow

def main():
    # Fix Qt platform plugin path issue
    try:
        import PyQt5
        pyqt5_path = os.path.dirname(PyQt5.__file__)
        
        # Try multiple possible plugin paths
        possible_paths = [
            os.path.join(pyqt5_path, 'Qt5', 'plugins'),
            os.path.join(pyqt5_path, 'Qt', 'plugins'),
            os.path.join(pyqt5_path, 'plugins'),
        ]
        
        # Also check site-packages
        try:
            import site
            for site_dir in site.getsitepackages():
                possible_paths.extend([
                    os.path.join(site_dir, 'PyQt5', 'Qt5', 'plugins'),
                    os.path.join(site_dir, 'PyQt5', 'Qt', 'plugins'),
                    os.path.join(site_dir, 'PyQt5', 'plugins'),
                ])
        except:
            pass
        
        # Find the first existing path
        for path in possible_paths:
            if os.path.exists(path):
                os.environ['QT_PLUGIN_PATH'] = path
                print(f"Setting QT_PLUGIN_PATH to: {path}")
                break
        else:
            print("Warning: Could not find Qt plugins directory")
            
    except Exception as e:
        print(f"Error setting Qt plugin path: {e}")
    
    # Set additional Qt environment variables
    os.environ['QT_QPA_PLATFORM_PLUGIN_PATH'] = os.environ.get('QT_PLUGIN_PATH', '')
    
    # Set platform-specific Qt settings
    if os.name == 'nt':  # Windows
        os.environ['QT_QPA_PLATFORM'] = 'windows'
    elif sys.platform == 'darwin':  # macOS
        os.environ['QT_QPA_PLATFORM'] = 'cocoa'
    
    app = QApplication(sys.argv)
    app.setApplicationName("Label Inspector")
    app.setOrganizationName("Label Inspector")
    
    # Set application style
    app.setStyleSheet("""
        QApplication {
            font-family: 'Segoe UI', Arial, sans-serif;
            font-size: 9pt;
        }
    """)
    
    window = MainWindow()
    window.show()
    
    sys.exit(app.exec_())

if __name__ == "__main__":
    main()
