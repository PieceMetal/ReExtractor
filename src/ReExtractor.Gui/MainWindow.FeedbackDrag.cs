using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
namespace ReExtractor.Gui;
public partial class MainWindow
{
 private Point? _feedbackPress;
 private Thickness _feedbackStartMargin;
 private bool _feedbackDragged;
 private void OnFeedbackDragPressed(object? sender, PointerPressedEventArgs e){
  if(!e.GetCurrentPoint(FeedbackLauncher).Properties.IsLeftButtonPressed)return;
  _feedbackPress=e.GetPosition(this);_feedbackStartMargin=FeedbackLauncher.Margin;_feedbackDragged=false;
  e.Pointer.Capture(FeedbackLauncher);e.Handled=true;
 }
 private void OnFeedbackDragMoved(object? sender, PointerEventArgs e){
  if(_feedbackPress is not Point start)return;
  var delta=e.GetPosition(this)-start;
  if(!_feedbackDragged&&Math.Abs(delta.X)<5&&Math.Abs(delta.Y)<5)return;
  _feedbackDragged=true;
  FeedbackLauncher.Margin=new Thickness(0,0,_feedbackStartMargin.Right-delta.X,_feedbackStartMargin.Bottom-delta.Y);
  ClampFeedbackLauncher();e.Handled=true;
 }
 private void OnFeedbackDragReleased(object? sender, PointerReleasedEventArgs e){
  if(_feedbackPress is null)return;
  var click=!_feedbackDragged;_feedbackPress=null;e.Pointer.Capture(null);e.Handled=true;
  if(click)OnFeedbackToggleClicked(sender,new RoutedEventArgs());
 }
 private void ClampFeedbackLauncher(){
  if(FeedbackLauncher.Parent is not Control parent)return;
  var margin=FeedbackLauncher.Margin;
  FeedbackLauncher.Margin=new Thickness(0,0,Math.Clamp(margin.Right,0,Math.Max(0,parent.Bounds.Width-FeedbackLauncher.Bounds.Width)),Math.Clamp(margin.Bottom,0,Math.Max(0,parent.Bounds.Height-FeedbackLauncher.Bounds.Height)));
 }
}
