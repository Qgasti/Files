// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Files.App.Helpers
{
	internal sealed partial class NavigationInteractionTracker : IDisposable
	{
		public bool CanNavigateForward
		{
			get
			{
				_props.TryGetBoolean(nameof(CanNavigateForward), out bool val);
				return val;
			}
			set
			{
				if (!_disposed)
				{
					_props.InsertBoolean(nameof(CanNavigateForward), value);
					_tracker.MaxPosition = new(value ? 96f : 0f);
				}
			}
		}

		public bool CanNavigateBackward
		{
			get
			{
				_props.TryGetBoolean(nameof(CanNavigateBackward), out bool val);
				return val;
			}
			set
			{
				if (!_disposed)
				{
					_props.InsertBoolean(nameof(CanNavigateBackward), value);
					_tracker.MinPosition = new(value ? -96f : 0f);
				}
			}
		}

		private UIElement _rootElement;
		private UIElement _backIcon;
		private UIElement _forwardIcon;

		private PointerEventHandler _pointerPressedHandler;

		private Visual _rootVisual;
		private Visual _backVisual;
		private Visual _forwardVisual;

		private InteractionTracker _tracker;
		private VisualInteractionSource _source;
		private InteractionTrackerOwner _trackerOwner;
		private CompositionPropertySet _props;
		private ExpressionAnimation _backAnimation;
		private ExpressionAnimation _forwardAnimation;

		private bool _isWindowActive = true;
		private bool _isInteractiveMoveActive;
		private bool _areAnimationsRunning;

		public event EventHandler<OverscrollNavigationEventArgs>? NavigationRequested;

		private bool _disposed;

		public NavigationInteractionTracker(UIElement rootElement, UIElement backIcon, UIElement forwardIcon)
		{
			_rootElement = rootElement;
			_backIcon = backIcon;
			_forwardIcon = forwardIcon;

			ElementCompositionPreview.SetIsTranslationEnabled(_backIcon, true);
			ElementCompositionPreview.SetIsTranslationEnabled(_forwardIcon, true);
			_rootVisual = ElementCompositionPreview.GetElementVisual(_rootElement);
			_backVisual = ElementCompositionPreview.GetElementVisual(_backIcon);
			_forwardVisual = ElementCompositionPreview.GetElementVisual(_forwardIcon);

			SetupInteractionTracker();

			_props = _rootVisual.Compositor.CreatePropertySet();
			CanNavigateBackward = false;
			CanNavigateForward = false;

			SetupAnimations();
			MainWindow.Instance.Activated += MainWindow_Activated;
			MainWindow.Instance.InteractiveMoveStarted += MainWindow_InteractiveMoveStarted;
			MainWindow.Instance.InteractiveMoveCompleted += MainWindow_InteractiveMoveCompleted;

			_pointerPressedHandler = new(PointerPressed);
			_rootElement.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, true);
		}

		[MemberNotNull(nameof(_tracker), nameof(_source), nameof(_trackerOwner))]
		private void SetupInteractionTracker()
		{
			var compositor = _rootVisual.Compositor;

			_trackerOwner = new(this);
			_tracker = InteractionTracker.CreateWithOwner(compositor, _trackerOwner);
			_tracker.MinPosition = new Vector3(-96f);
			_tracker.MaxPosition = new Vector3(96f);

			_source = VisualInteractionSource.Create(_rootVisual);
			_source.ManipulationRedirectionMode = VisualInteractionSourceRedirectionMode.CapableTouchpadOnly;
			_source.PositionXSourceMode = InteractionSourceMode.EnabledWithoutInertia;
			_source.PositionXChainingMode = InteractionChainingMode.Always;
			_source.PositionYSourceMode = InteractionSourceMode.Disabled;
			_tracker.InteractionSources.Add(_source);
		}

		[MemberNotNull(nameof(_backAnimation), nameof(_forwardAnimation))]
		private void SetupAnimations()
		{
			var compositor = _rootVisual.Compositor;

			var backResistance = CreateResistanceCondition(-96f, 0f);
			var forwardResistance = CreateResistanceCondition(0f, 96f);
			List<CompositionConditionalValue> conditionalValues = [backResistance, forwardResistance];
			_source.ConfigureDeltaPositionXModifiers(conditionalValues);

			_backAnimation = compositor.CreateExpressionAnimation("(-clamp(tracker.Position.X, -96, 0) * 2) - 48");
			_backAnimation.SetReferenceParameter("tracker", _tracker);

			_forwardAnimation = compositor.CreateExpressionAnimation("(-clamp(tracker.Position.X, 0, 96) * 2) + 48");
			_forwardAnimation.SetReferenceParameter("tracker", _tracker);

			ResumeAnimations();
		}

		private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
		{
			_isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
			UpdateAnimationState();
		}

		private void MainWindow_InteractiveMoveStarted(object? sender, EventArgs e)
		{
			_isInteractiveMoveActive = true;
			UpdateAnimationState();
		}

		private void MainWindow_InteractiveMoveCompleted(object? sender, EventArgs e)
		{
			_isInteractiveMoveActive = false;
			UpdateAnimationState();
		}

		private void UpdateAnimationState()
		{
			if (_isWindowActive && !_isInteractiveMoveActive)
				ResumeAnimations();
			else
				PauseAnimations();
		}

		private void ResumeAnimations()
		{
			if (_disposed || _areAnimationsRunning)
				return;

			_backVisual.StartAnimation("Translation.X", _backAnimation);
			_forwardVisual.StartAnimation("Translation.X", _forwardAnimation);
			_areAnimationsRunning = true;
		}

		private void PauseAnimations()
		{
			if (_disposed || !_areAnimationsRunning)
				return;

			_tracker.TryUpdatePosition(new(0f));
			_backVisual.StopAnimation("Translation.X");
			_forwardVisual.StopAnimation("Translation.X");
			_backVisual.Properties.InsertVector3("Translation", new(-48f, 0f, 0f));
			_forwardVisual.Properties.InsertVector3("Translation", new(48f, 0f, 0f));
			_areAnimationsRunning = false;
		}

		private void PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
			{
				_source.TryRedirectForManipulation(e.GetCurrentPoint(_rootElement));
			}
		}

		private CompositionConditionalValue CreateResistanceCondition(float minValue, float maxValue)
		{
			var compositor = _rootVisual.Compositor;

			var resistance = CompositionConditionalValue.Create(compositor);
			var resistanceCondition = compositor.CreateExpressionAnimation($"tracker.Position.X > {minValue} && tracker.Position.X < {maxValue}");
			resistanceCondition.SetReferenceParameter("tracker", _tracker);
			var resistanceValue = compositor.CreateExpressionAnimation($"source.DeltaPosition.X * (1 - sqrt(1 - square((tracker.Position.X / {minValue + maxValue}) - 1)))");
			resistanceValue.SetReferenceParameter("source", _source);
			resistanceValue.SetReferenceParameter("tracker", _tracker);
			resistance.Condition = resistanceCondition;
			resistance.Value = resistanceValue;

			return resistance;
		}

		~NavigationInteractionTracker()
		{
			Dispose();
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;

			MainWindow.Instance.Activated -= MainWindow_Activated;
			MainWindow.Instance.InteractiveMoveStarted -= MainWindow_InteractiveMoveStarted;
			MainWindow.Instance.InteractiveMoveCompleted -= MainWindow_InteractiveMoveCompleted;
			_rootElement.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
			_backVisual.StopAnimation("Translation.X");
			_forwardVisual.StopAnimation("Translation.X");
			_backAnimation.Dispose();
			_forwardAnimation.Dispose();
			_tracker.Dispose();
			_source.Dispose();
			_props.Dispose();

			GC.SuppressFinalize(this);
		}

		private sealed partial class InteractionTrackerOwner : IInteractionTrackerOwner
		{
			private NavigationInteractionTracker _parent;
			private bool _shouldBounceBack;
			private bool _shouldAnimate = true;
			private Vector3KeyFrameAnimation _scaleAnimation;
			private SpringVector3NaturalMotionAnimation _returnAnimation;

			public InteractionTrackerOwner(NavigationInteractionTracker parent)
			{
				_parent = parent;

				var compositor = _parent._rootVisual.Compositor;
				_scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
				_scaleAnimation.InsertKeyFrame(0.5f, new(1.3f));
				_scaleAnimation.InsertKeyFrame(1f, new(1f));
				_scaleAnimation.Duration = TimeSpan.FromMilliseconds(275);

				_returnAnimation = compositor.CreateSpringVector3Animation();
				_returnAnimation.FinalValue = new(0f);
				_returnAnimation.DampingRatio = 1f;
			}

			public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
			{
				if (!_shouldBounceBack)
					return;

				if (Math.Abs(sender.Position.X) > 64)
				{
					_parent._tracker.TryUpdatePosition(new(0f));

					EventHandler<OverscrollNavigationEventArgs>? navEvent = _parent.NavigationRequested;
					if (navEvent is not null)
					{
						if (sender.Position.X > 0 && _parent.CanNavigateForward)
						{
							navEvent(_parent, OverscrollNavigationEventArgs.Forward);
						}
						else if (sender.Position.X < 0 && _parent.CanNavigateBackward)
						{
							navEvent(_parent, OverscrollNavigationEventArgs.Back);
						}
					}
				}
				else
				{
					_parent._tracker.TryUpdatePositionWithAnimation(_returnAnimation);
				}
				_shouldBounceBack = false;
				_shouldAnimate = true;
			}

			public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
			{
				_shouldBounceBack = true;
			}

			public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
			{
				if (!_shouldAnimate)
					return;

				if (args.Position.X <= -64)
				{
					_parent._backVisual.StartAnimation("Scale", _scaleAnimation);
					_shouldAnimate = false;
				}
				else if (args.Position.X >= 64)
				{
					_parent._forwardVisual.StartAnimation("Scale", _scaleAnimation);
					_shouldAnimate = false;
				}

			}

			// required to implement IInteractionTrackerOwner
			public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }
			public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }
			public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }
		}
	}

	public enum OverscrollNavigationEventArgs
	{
		Back,
		Forward
	}
}
