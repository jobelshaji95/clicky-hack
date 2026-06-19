// This project enables both WPF (UseWPF) and WinForms (UseWindowsForms) so it can
// use Screen.AllScreens / System.Drawing for multi-monitor bounds. That pulls
// System.Windows.Forms and System.Drawing into scope globally, which collide with
// the WPF types of the same name (Application, MessageBox, Point, Size, Color).
//
// Clicky is a WPF app, so these unqualified names should always mean the WPF type.
// The few GDI files that need the System.Drawing types use distinct names
// (Rectangle, Bitmap, Graphics) or fully-qualified references.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using Color = System.Windows.Media.Color;
