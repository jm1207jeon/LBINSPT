from PyQt5.QtWidgets import QScrollArea, QLabel
from PyQt5.QtCore import Qt, QPoint, pyqtSignal
from PyQt5.QtGui import QPixmap, QCursor

class ZoomableScrollArea(QScrollArea):
    zoom_changed = pyqtSignal(int)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.zoom_factor = 1.0
        self.min_zoom = 0.1
        self.max_zoom = 5.0
        self.zoom_step = 0.3  # Changed to 30% per mouse wheel scroll
        
        # Pan functionality
        self.pan_start_point = QPoint()
        self.panning = False
        self.last_pan_point = QPoint()
        self.drag_mode = False
        
        # Enable mouse tracking
        self.setMouseTracking(True)
        
        # Enable scrollbars - always show when needed
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        
        # Ensure widget resizing works properly
        self.setWidgetResizable(False)  # Don't auto-resize widget to fit
        
    def setDragMode(self, enabled):
        """Enable or disable drag mode"""
        self.drag_mode = enabled
        
    def wheelEvent(self, event):
        """Handle mouse wheel for zooming centered on mouse position"""
        if event.modifiers() == Qt.ControlModifier or True:  # Always allow zoom with wheel
            # Get current scroll positions and mouse position
            h_bar = self.horizontalScrollBar()
            v_bar = self.verticalScrollBar()
            old_h_value = h_bar.value() if h_bar else 0
            old_v_value = v_bar.value() if v_bar else 0
            
            # Get mouse position relative to the scroll area
            mouse_pos = event.pos()
            
            # Calculate zoom
            zoom_in = event.angleDelta().y() > 0
            old_zoom = self.zoom_factor
            
            if zoom_in:
                new_zoom = min(self.zoom_factor + self.zoom_step, self.max_zoom)
            else:
                new_zoom = max(self.zoom_factor - self.zoom_step, self.min_zoom)
            
            if new_zoom != self.zoom_factor:
                self.zoom_factor = new_zoom
                self.zoom_changed.emit(int(self.zoom_factor * 100))
                
                # Calculate new scroll positions to keep mouse point centered
                zoom_ratio = new_zoom / old_zoom
                
                # Adjust scroll positions based on mouse position
                if h_bar:
                    new_h_value = (old_h_value + mouse_pos.x()) * zoom_ratio - mouse_pos.x()
                    h_bar.setValue(int(new_h_value))
                
                if v_bar:
                    new_v_value = (old_v_value + mouse_pos.y()) * zoom_ratio - mouse_pos.y()
                    v_bar.setValue(int(new_v_value))
        else:
            super().wheelEvent(event)
    
    def mapToScene(self, point):
        """Map widget coordinates to scene coordinates"""
        widget = self.widget()
        if widget:
            return self.mapFromGlobal(self.mapToGlobal(point))
        return point
    
    def centerOn(self, point):
        """Center the view on a specific point"""
        # This is a simplified version - in a full implementation,
        # you'd want to properly handle the coordinate transformation
        pass
    
    def mousePressEvent(self, event):
        """Handle mouse press for panning"""
        if event.button() == Qt.LeftButton and self.drag_mode:
            self.pan_start_point = event.pos()
            self.last_pan_point = event.pos()
            self.panning = True
            self.setCursor(QCursor(Qt.ClosedHandCursor))
            event.accept()
            return
        super().mousePressEvent(event)
    
    def mouseMoveEvent(self, event):
        """Handle mouse move for panning"""
        if self.panning and (event.buttons() & Qt.LeftButton) and self.drag_mode:
            # Calculate the difference from last position
            delta = event.pos() - self.last_pan_point
            
            # Get scrollbars
            h_bar = self.horizontalScrollBar()
            v_bar = self.verticalScrollBar()
            
            # Move the scrollbars in opposite direction of mouse movement
            if h_bar:
                new_h_value = h_bar.value() - delta.x()
                h_bar.setValue(int(new_h_value))
            if v_bar:
                new_v_value = v_bar.value() - delta.y()
                v_bar.setValue(int(new_v_value))
            
            # Update last position for continuous dragging
            self.last_pan_point = event.pos()
            event.accept()
            return
        elif not self.panning and self.drag_mode:
            # Show appropriate cursor when not panning but drag mode is enabled
            if self.widget() and hasattr(self.widget(), 'pixmap') and self.widget().pixmap():
                self.setCursor(QCursor(Qt.OpenHandCursor))
            else:
                self.setCursor(QCursor(Qt.ArrowCursor))
        
        super().mouseMoveEvent(event)
    
    def mouseReleaseEvent(self, event):
        """Handle mouse release"""
        if event.button() == Qt.LeftButton and self.drag_mode:
            self.panning = False
            if self.widget() and hasattr(self.widget(), 'pixmap') and self.widget().pixmap():
                self.setCursor(QCursor(Qt.OpenHandCursor))
            else:
                self.setCursor(QCursor(Qt.ArrowCursor))
        super().mouseReleaseEvent(event)
    
    def enterEvent(self, event):
        """Handle mouse enter"""
        if self.drag_mode and self.widget() and hasattr(self.widget(), 'pixmap') and self.widget().pixmap():
            self.setCursor(QCursor(Qt.OpenHandCursor))
        super().enterEvent(event)
    
    def leaveEvent(self, event):
        """Handle mouse leave"""
        self.setCursor(QCursor(Qt.ArrowCursor))
        super().leaveEvent(event)
