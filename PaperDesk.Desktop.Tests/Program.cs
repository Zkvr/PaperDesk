using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PaperDesk.Core;
using PaperDesk.Storage;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.ViewModels;
using PaperDesk.Desktop.Views;
using System.Windows.Interop;
using System.Windows.Threading;

static class UiChecks
{
    [STAThread] static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Require(PaperDesk.Core.L.T("Все заметки") == "All notes", "English UI strings must load from the embedded resource");
        Require(PaperDesk.Core.L.T("Стопки на столе ▾") == "Desktop stacks ▾", "Stack controls must be localized");
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        Require(PaperDesk.Core.L.T("Все заметки") == "Все заметки", "Russian UI strings must follow the UI culture");
        var app=new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("pack://application:,,,/PaperDesk;component/Controls/Theme.xaml") });
        if (args is ["--web-editor", var editorFolder]) return WebEditorChecks.Run(editorFolder);
        if (args is ["--web-editor-fallback", var fallbackFolder]) return WebEditorChecks.Run(fallbackFolder, withoutRuntime: true);
        var body=new NoteBodyEditor { Width=270,Height=180,FontSize=17,Text="☐ В банк\n☑ В сервис\nОбычная строка" };
        Layout(body);var boxes=Descendants(body).OfType<CheckBox>().ToList();
        Require(boxes.Count==2,"Unicode markers must become real CheckBox controls");
        var folder=args.Length>0?args[0]:Path.Combine(AppContext.BaseDirectory,"test-artifacts");Directory.CreateDirectory(folder);
        using var store=new NoteStore(Path.Combine(folder,"ui-checks.db"));
        var note=new Note{Body=body.Text};store.Save(note);body.TextChanged+=(_,_)=>store.Save(store.Find(note.Id)!.Note with{Body=body.Text});
        boxes[0].IsChecked=true;boxes[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(store.Find(note.Id)!.Note.Body=="☑ В банк\n☑ В сервис\nОбычная строка","A checkbox click must persist the changed marker");
        Console.WriteLine("PASS actual WPF checkbox click persists through SQLite");
        body.BeginEdit();
        Layout(body);
        var field = Descendants(body).OfType<PaperLineInput>().Single(x => x.Visibility == Visibility.Visible);
        Require(field.Text == "Обычная строка" && Descendants(body).OfType<CheckBox>().Count() == 2 &&
            Descendants(body).OfType<ScrollViewer>().Single(x => x.Content is StackPanel).Visibility == Visibility.Visible,
            $"Editing a paragraph must keep the real checkboxes on screen (active text: {field.Text})");
        field.Text = "Исправленная строка";
        body.EndEdit();
        Layout(body);
        Require(store.Find(note.Id)!.Note.Body.EndsWith("Исправленная строка"), "A visual paragraph edit must save normally");
        Console.WriteLine("PASS visual paragraph editing keeps interactive task controls visible");
        body.FontSize = 12;
        Layout(body);
        var smallCheck = Descendants(body).OfType<CheckBox>().First().ActualWidth;
        body.FontSize = 32;
        Layout(body);
        var largeCheck = Descendants(body).OfType<CheckBox>().First();
        Require(largeCheck.ActualWidth > smallCheck * 2 && largeCheck.IsChecked == true,
            "Checkbox glyphs must scale with the text while preserving the marked state");
        Console.WriteLine("PASS checkbox glyphs scale with note typography");

        var bodyViewer = Descendants(body).OfType<ScrollViewer>().First(x => x.Content is StackPanel);
        var editableLabel = ((StackPanel)bodyViewer.Content).Children.OfType<Grid>().First().Children.OfType<TextBlock>().First();
        bodyViewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = editableLabel
        });
        Require(body.IsEditing && body.CaretIndex < body.Text.Length, "A single click on a task label must start writing on that line");
        var taskField = Descendants(body).OfType<PaperLineInput>().Single(x => x.Visibility == Visibility.Visible);
        Require(taskField.Text == "В банк", "The task field must not show its checkbox markup");
        taskField.Text = "В офис";
        Require(body.Text.StartsWith("☑ В офис", StringComparison.Ordinal), "Editing the task label must preserve checked state");
        body.EndEdit();
        Console.WriteLine("PASS single click on note text starts inline editing");
        var blocks = new NoteBodyEditor { Width = 400, Height = 270, FontSize = 17,
            Text = "- [ ] Задача\n| День | План |\n| --- | --- |\n| Пн | Поездка |\n:::spoiler Детали\nТекст внутри\n:::" };
        Layout(blocks);
        ClickBlock("План").Text = "Маршрут";
        Require(blocks.Text.Contains("| День | Маршрут |", StringComparison.Ordinal) &&
            Descendants(blocks).OfType<CheckBox>().Single().Visibility == Visibility.Visible, "A table cell must save without exposing its pipe delimiters");
        Layout(blocks);
        SaveSized(blocks, Path.Combine(folder, "visual-table-editing.png"));
        blocks.EndEdit();
        Layout(blocks);
        ClickBlock("Детали").Text = "Что взять";
        Require(blocks.Text.Contains(":::spoiler Что взять\nТекст внутри\n:::", StringComparison.Ordinal) &&
            Descendants(blocks).OfType<Button>().Any(b => Equals(b.Content, "▸") && b.Visibility == Visibility.Visible),
            "Editing the spoiler title must leave a real disclosure and preserve its contents");
        blocks.EndEdit();
        Console.WriteLine("PASS visual table and spoiler fields preserve the structured note while typing");
        var beforeSpoiler = "Оставить";
        var spoilerEdit = NoteFormat.Apply(beforeSpoiler, beforeSpoiler.Length, 0, NoteFormat.Kind.Spoiler);
        Require(spoilerEdit.Text == "\n:::spoiler\n\n:::\n", "A new spoiler must contain an empty body and no stored placeholder");
        var codeEdit = NoteFormat.Apply("", 0, 0, NoteFormat.Kind.Code);
        Require(codeEdit.Text == "", "Code must not insert sample text without a selection");
        var listText = "- [ ] Пункт\n- Строка";
        var boldEdit = NoteFormat.Apply(listText, 0, listText.Length, NoteFormat.Kind.Bold);
        Require(boldEdit.Text == "- [ ] **Пункт**\n- **Строка**", "Formatting multiple rows must leave list markers outside the style");
        CheckFormatting(folder);

        PaperLineInput ClickBlock(string label)
        {
            var caption = Descendants(blocks).OfType<TextBlock>().Single(c => c.Text == label && c.Visibility == Visibility.Visible);
            var scroll = Descendants(blocks).OfType<ScrollViewer>().Single(v => v.Content is StackPanel);
            scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = caption
            });
            var input = Descendants(blocks).OfType<PaperLineInput>().Single(x => x.Visibility == Visibility.Visible);
            Require(input.Text == label, "The active field must display only the visible label");
            return input;
        }
        var ink=((SolidColorBrush)app.FindResource("Ink")).Color;
        Require(Contrast(ink,Color.FromRgb(27,30,36))>10,"Dark theme foreground must be readable");
        foreach(var (_,color) in PaperPalette.Colors)Require(Contrast(Color.FromRgb(48,45,41),PaperPalette.Brush(color).Color)>8,"Paper text contrast");
        Console.WriteLine("PASS contrast exceeds 10:1 on graphite and 8:1 on every paper");
        var picker=new PaperColorPicker{SelectedColor="#F4E5AD",Width=208,Height=34};picker.ColorSelected+=color=>store.SavePlacement(new Placement{NoteId=note.Id,Color=color});Layout(picker);var colorButtons=Descendants(picker).OfType<Button>().ToList();
        Require(colorButtons.Count==6&&colorButtons.All(b=>b.Content is System.Windows.Shapes.Ellipse),"Color choices must be visible swatches");colorButtons[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Require(store.GetPlacement(note.Id).Color=="#CDDCE9","Swatch click must persist the selected color");
        Console.WriteLine("PASS visual color swatches persist the selected color");
        var paperButton=new Button{Style=(Style)app.FindResource("PaperButton"),Content="2 / 2  ⌄",Width=210,Height=36};Layout(paperButton);
        var renderedLabel=Descendants(paperButton).OfType<TextBlock>().First(x=>x.Text.Contains("2 / 2"));
        foreach(var (_,color) in PaperPalette.Colors)Require(Contrast(((SolidColorBrush)renderedLabel.Foreground).Color,PaperPalette.Brush(color).Color)>4.5,"Rendered paper button text must inherit dark ink rather than the application white");
        var darkLabel=new TextBlock{Text="Все заметки"};var darkHost=new Grid{Width=200,Height=35};darkHost.SetValue(System.Windows.Documents.TextElement.ForegroundProperty,app.FindResource("Ink"));darkHost.Children.Add(darkLabel);Layout(darkHost);Require(Contrast(((SolidColorBrush)darkLabel.Foreground).Color,Color.FromRgb(27,30,36))>10,"Manager labels must retain their light foreground");
        Console.WriteLine("PASS rendered paper button labels remain readable on every paper");
        CheckDesktopStacks(folder);
        CheckTrashGrid(folder);
        CheckPictures(folder);
        CheckPeelAndDrop(folder);
        var longBody=new NoteBodyEditor{Width=270,Height=90,FontSize=17,Text=string.Join('\n',Enumerable.Range(1,20).Select(i=>"Строка "+i))};Layout(longBody);
        var scroller=Descendants(longBody).OfType<ScrollViewer>().First(x=>x.Content is StackPanel);scroller.ScrollToVerticalOffset(9);Layout(longBody);System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Background);Layout(longBody);
        longBody.Height=65;Layout(longBody);System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Background);Layout(longBody);
        var linePanel=(StackPanel)scroller.Content;var firstVisible=linePanel.Children.OfType<FrameworkElement>().First(x=>x.TransformToAncestor(scroller).Transform(new Point(0,x.ActualHeight)).Y>1);
        Require(firstVisible.TransformToAncestor(scroller).Transform(new Point()).Y>=-.5,"The first visible line must not be vertically clipped");
        RequireWholeRows(linePanel);
        var longTask = "Подготовить отчёт и отправить коллеге до пятницы. 🌿 ";
        longBody.Width = 175;
        longBody.Height = 49;
        longBody.FontSize = 32;
        longBody.Text = "☐ " + longTask + "\r\nКонец";
        SettleLayout(longBody);
        RequireWholeRows(linePanel);
        var taskRows = linePanel.Children.OfType<Grid>().SkipLast(1).ToList();
        Require(taskRows.Count > 2 && string.Concat(taskRows.Select(row => ((TextBlock)row.Children[0]).Text)) == longTask,
            "A task taller than the viewport must wrap to individually scrollable lines without losing text");
        Require(Descendants(linePanel).OfType<CheckBox>().Count() == 1, "A wrapped task must have only one checkbox");
        scroller.ScrollToEnd();
        SettleLayout(longBody);
        RequireWholeRows(linePanel);
        var last = (FrameworkElement)linePanel.Children[^1];
        var lastBounds = last.TransformToAncestor(linePanel).TransformBounds(new Rect(last.RenderSize));
        Require(lastBounds.Top >= -.5 && lastBounds.Bottom <= linePanel.Clip.Bounds.Bottom + .5,
            "The end of a long note must scroll fully into view");
        Console.WriteLine("PASS scrolling preserves full rows at both edges, including wrapped tasks and the last row");
        var sizePicker=new NoteSizePicker{Width=208,Height=36};Size requested=new();sizePicker.SizeSelected+=s=>requested=s;Layout(sizePicker);Descendants(sizePicker).OfType<Button>().ElementAt(1).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Require(requested==new Size(330,370),"A visible size preset must request real window dimensions");
        Console.WriteLine("PASS visible note-size preset requests the selected dimensions");
        Descendants(sizePicker).OfType<Button>().First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Require(requested==new Size(240,220),"The small preset must be smaller than the former 260 by 300 limit");
        var stackNotes=new[]{new Note(),new Note(),new Note()};
        for(var i=0;i<stackNotes.Length;i++)store.SavePlacement(new Placement{NoteId=stackNotes[i].Id,Color=PaperPalette.Colors[i].Color});
        var behind=PaperStack.ColorsBehind(stackNotes,stackNotes[2].Id,id=>store.GetPlacement(id).Color);
        Require(behind.SequenceEqual(new[]{PaperPalette.Colors[0].Color,PaperPalette.Colors[1].Color}),"Backing sheets must follow actual note colors across the cycle boundary");
        store.SavePlacement(store.GetPlacement(stackNotes[0].Id) with{Color=PaperPalette.Colors[4].Color});
        Require(PaperStack.ColorsBehind(stackNotes.Take(2).ToList(),stackNotes[1].Id,id=>store.GetPlacement(id).Color).SequenceEqual(new[]{PaperPalette.Colors[4].Color}),"Two-note stack must expose only the other note and reflect its changed color");
        Require(PaperStack.ColorsBehind(stackNotes.Take(1).ToList(),stackNotes[0].Id,_=>"#000000").Count==0,"Single note has no fake backing sheets");
        Console.WriteLine("PASS stack backings use persisted colors of the next real notes");
        foreach(var scale in new[]{1d,1.25,1.5,2})
        {
            var original=new Rect(430,250,240,220);
            foreach(var physicalDelta in new[]{new Vector(-1000,-1000),new Vector(2000,0),new Vector(0,-250*scale)})
            {
                var moved=PaperDrag.MovedBounds(original,physicalDelta,new Matrix(1/scale,0,0,1/scale,0,0));
                Require(moved.Size==original.Size&&Math.Abs(moved.Left-(original.Left+physicalDelta.X/scale))<.01&&Math.Abs(moved.Top-(original.Top+physicalDelta.Y/scale))<.01,"Dragging to any screen edge must change position only, with physical-to-DIP conversion");
            }
        }
        Console.WriteLine("PASS custom dragging preserves sheet size at edges for 100–200% DPI");
        var gesture = new PaperDragTrack(new Point(800, 450), new Rect(440, 260, 240, 220), new Matrix(.8, 0, 0, .8, 0, 0), 0);
        var sampleTime = System.Diagnostics.Stopwatch.Frequency / 60;
        Require(gesture.TryMove(new Point(950, 510), sampleTime, out var dragged, out var dragSpeed) &&
            dragged == new Rect(560, 308, 240, 220) && dragSpeed.X > 0 && dragSpeed.Y > 0,
            "A screen sample must move the window and bend in the same direction");
        for (var index = 2; index < 25; index++)
            Require(!gesture.TryMove(new Point(950, 510), sampleTime * index, out _, out _),
                "Repeated mouse moves caused by the changing visual must not feed back into the drag");
        Require(gesture.TryMove(new Point(800, 450), sampleTime * 26, out var reversed, out var reverseSpeed) &&
            reversed == new Rect(440, 260, 240, 220) && reverseSpeed.X < 0 && reverseSpeed.Y < 0,
            "Reversing the cursor must return the window to the original anchor without drift");
        Console.WriteLine("PASS repeated frame samples cannot move the window or feed the drag animation");
        var pin = PaperDesk.Desktop.Views.StickyWindow.DragPin;
        var rightSwing = PaperDesk.Desktop.Views.StickyWindow.SwingAngle(1, 204, 152, new Thickness(8), .925, .963);
        var leftSwing = PaperDesk.Desktop.Views.StickyWindow.SwingAngle(-1, 204, 152, new Thickness(8), .925, .963);
        Require(pin.X == .5 && pin.Y < .1 && rightSwing > 4 && Math.Abs(leftSwing + rightSwing) < .01,
            "A compact sheet must visibly swing both ways around the same top-center pin");
        Console.WriteLine("PASS the sheet swings both ways around a fixed top-center pin");
        var compactPaper=new PaperSurface{Width=StickyLayout.MinWidth-16,Height=StickyLayout.MinHeight-28,Fill=PaperPalette.Brush("#F4E5AD")};
        var compactContent=new Grid();foreach(var height in new[]{new GridLength(32),GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})compactContent.RowDefinitions.Add(new RowDefinition{Height=height});
        var compactToolbar=new Grid();var toolbarButtons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};foreach(var caption in new[]{"⌖","○","•••","−","\uE74D","×"})toolbarButtons.Children.Add(new Button{Content=caption,Style=(Style)app.FindResource("PaperButton"),FontFamily=new FontFamily(caption=="\uE74D"?"Segoe MDL2 Assets":"Segoe UI")});compactToolbar.Children.Add(toolbarButtons);compactContent.Children.Add(compactToolbar);
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var compactTitle=new TextBox{Text="Прочитать",Style=(Style)app.FindResource("PaperEditor"),FontFamily=new FontFamily("Segoe Print")};
        titleRow.Children.Add(compactTitle);
        var conflict = new Button { Content = "⚠", Style = (Style)app.FindResource("PaperButton") };
        Grid.SetColumn(conflict,1); titleRow.Children.Add(conflict);
        Grid.SetRow(titleRow,1); compactContent.Children.Add(titleRow);
        var compactBody=new NoteBodyEditor{FontSize=17,Text="☐ Статья про UX\n☐ Идеи для релиза\nТретья строка"};Grid.SetRow(compactBody,2);compactContent.Children.Add(compactBody);
        var compactNav=new Grid();compactNav.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(30)});compactNav.ColumnDefinitions.Add(new ColumnDefinition());compactNav.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(30)});var navIndex=0;foreach(var caption in new[]{"‹","1 / 2  ⌄","›"}){var button=new Button{Content=caption,Style=(Style)app.FindResource("PaperButton")};Grid.SetColumn(button,navIndex++);compactNav.Children.Add(button);}Grid.SetRow(compactNav,3);compactContent.Children.Add(compactNav);
        compactPaper.Child=compactContent;StickyLayout.Apply(compactContent,true);Layout(compactPaper);
        SaveRender(compactPaper,Path.Combine(folder,"compact-minimum.png"));
        Require(compactBody.ActualHeight>=45,$"Even at minimum size, the body needs a whole task line and its own scroll viewport (body={compactBody.ActualHeight}, title={compactTitle.ActualHeight}, nav={compactNav.ActualHeight})");
        Require(toolbarButtons.ActualWidth+17<=compactToolbar.ActualWidth,"Compact toolbar must leave a separate drag handle");
        var compactCheck=Descendants(compactBody).OfType<CheckBox>().First();Require(compactCheck.ActualHeight<=compactBody.ActualHeight,"First complete task must fit at minimum size");compactCheck.IsChecked=true;compactCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Require(compactBody.Text.StartsWith("☑"),"Tasks must remain clickable at minimum size");
        SaveRender(compactPaper,Path.Combine(folder,"compact-minimum.png"));
        StickyLayout.Apply(compactContent,false);Require(compactTitle.FontSize==21,"Growing a sheet restores regular title size");
        Console.WriteLine("PASS minimum-size layout leaves readable, interactive body content");
        var palettePanel=new StackPanel{Margin=new Thickness(14)};palettePanel.Children.Add(new TextBlock{Text="Цвет бумаги",FontSize=11,Foreground=(Brush)app.FindResource("MutedInk"),Margin=new Thickness(3,0,0,9)});palettePanel.Children.Add(picker);var paletteFrame=new Border{Width=238,Height=93,Background=new SolidColorBrush(Color.FromRgb(37,42,50)),CornerRadius=new CornerRadius(12),Child=palettePanel};Layout(paletteFrame);SaveRender(paletteFrame,Path.Combine(folder,"color-picker.png"));
        var surface=new PaperSurface{Width=330,Height=370,Fill=PaperPalette.Brush("#F4E5AD")};
        var layout=new Grid{Margin=new Thickness(25,28,25,28)};layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.Children.Add(new TextBlock{Text="Позвонить",FontFamily=new FontFamily("Segoe Print"),FontSize=23,FontWeight=FontWeights.SemiBold,Foreground=PaperPalette.Brush("#302D29"),Margin=new Thickness(0,0,0,20)});
        var view=new NoteBodyEditor{Text="☑ В банк\n☐ В сервис\n☐ Коллеге",FontSize=17};Grid.SetRow(view,1);layout.Children.Add(view);
        var nav=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,13,0,0)};foreach(var caption in new[]{"‹","1 / 6  ⌄","›"})nav.Children.Add(new Button{Content=caption,Style=(Style)app.FindResource("PaperButton"),FontSize=caption.Length==1?24:13,Padding=new Thickness(6,3,6,3)});Grid.SetRow(nav,2);layout.Children.Add(nav);surface.Child=layout;
        Layout(surface);var bmp=new RenderTargetBitmap(330,370,96,96,PixelFormats.Pbgra32);bmp.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using(var stream=File.Create(Path.Combine(folder,"paper-controls.png")))encoder.Save(stream);
        var motionPaper = new PaperSurface { Width = 210, Height = 175, Fill = PaperPalette.Brush("#F4E5AD") };
        Layout(motionPaper);
        var restingAlpha = CornerAlpha(motionPaper);
        var curl = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1));
        var clock = (AnimationClock)curl.CreateClock(true);
        motionPaper.ApplyAnimationClock(PaperSurface.CurlLiftProperty, clock);
        clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(950), TimeSeekOrigin.BeginTime);
        Layout(motionPaper);
        var liftedAlpha = CornerAlpha(motionPaper);
        Require(restingAlpha > liftedAlpha + 100, $"Lifting the corner must visibly change the sheet silhouette ({restingAlpha} to {liftedAlpha}, lift {motionPaper.CurlLift:0.00})");
        motionPaper.ApplyAnimationClock(PaperSurface.CurlLiftProperty, null);
        Layout(motionPaper);
        Require(CornerAlpha(motionPaper) == restingAlpha, "The corner must return to its resting silhouette");
        Console.WriteLine("PASS animated corner visibly lifts and returns to its resting shape");
        var corners = new[] { new Int32Rect(10,10,1,1), new Int32Rect(200,10,1,1), new Int32Rect(10,165,1,1), new Int32Rect(200,165,1,1) };
        var rest = Render(motionPaper);
        foreach (var (name, pull, raised, flat) in new[]
        {
            ("right", new Vector(1,0), new[]{2}, new[]{3}),
            ("left", new Vector(-1,0), new[]{3}, new[]{2}),
            ("up", new Vector(0,-1), new[]{2,3}, new[]{0,1}),
            ("down", new Vector(0,1), new[]{0,1}, new[]{2,3})
        })
        {
            motionPaper.DragPull = pull;
            Layout(motionPaper);
            var rendered = Render(motionPaper);
            Require(raised.All(i => PixelAlpha(rest, corners[i]) - PixelAlpha(rendered, corners[i]) > 100) &&
                flat.All(i => PixelAlpha(rest, corners[i]) == PixelAlpha(rendered, corners[i])),
                $"Dragging {name} must lift the trailing corners and keep the leading corners flat");
        }
        motionPaper.DragPull = default;
        motionPaper.Child = new TextBlock
        {
            Text = "Записка\n\nВстретимся в семь.", FontSize = 18,
            Foreground = PaperPalette.Brush("#302D29"), Margin = new Thickness(17)
        };
        Layout(motionPaper);
        Console.WriteLine("PASS paper curls behind motion in all four directions");

        var crumple = new PaperCrumple { Width = motionPaper.Width, Height = motionPaper.Height };
        Require(crumple.TryPlay(motionPaper, () => {}), "Trash animation must be able to take a sheet snapshot");
        crumple.BeginAnimation(PaperCrumple.ProgressProperty, null);
        crumple.Progress = .68;
        Layout(crumple);
        var bundle = Render(crumple);
        Require(PixelAlpha(bundle, new Int32Rect(108,85,1,1)) > 180 &&
            PixelAlpha(bundle, new Int32Rect(30,30,1,1)) < 10 &&
            PixelAlpha(bundle, new Int32Rect(174,141,1,1)) < 10,
            "The crumple must gather the sheet into a visible bundle rather than dissolve a full rectangle");
        SaveRender(crumple, Path.Combine(folder, "crumple-bundle.png"));
        crumple.Progress = .39;
        Layout(crumple);
        SaveRender(crumple, Path.Combine(folder, "crumple-folding.png"));
        crumple.Progress = 1;
        Layout(crumple);
        Require(PixelAlpha(Render(crumple), new Int32Rect(108,85,1,1)) == 0, "The bundle must disappear after falling");
        crumple.Cancel();
        var finished = false;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        deadline.Tick += (_, _) => frame.Continue = false;
        var crumpleTime = System.Diagnostics.Stopwatch.StartNew();
        Require(crumple.TryPlay(motionPaper, () => { finished = true; frame.Continue = false; }), "Crumple must be reusable");
        deadline.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        deadline.Stop();
        Require(finished && !crumple.IsPlaying && crumple.Visibility == Visibility.Collapsed && crumpleTime.ElapsedMilliseconds >= 800,
            "The production animation must hold long enough to read as a crumple and let the next sheet appear afterward");
        Console.WriteLine("PASS crumple gathers the actual sheet and completes its lifecycle");
        Console.WriteLine("WPF checks passed; renders saved");return 0;
    }

    private static void CheckTrashGrid(string folder)
    {
        using var desk = new DeskController(Path.Combine(folder, "trash-grid-" + Guid.NewGuid().ToString("N")));
        for (var i = 0; i < 12; i++)
            desk.Store.Save(new Note { Title = $"Черновик {i + 1}", DeletedAt = DateTimeOffset.UtcNow });

        var manager = new ManagerWindow(desk) { Width = 1220, Height = 800, ShowActivated = false };
        manager.Show();
        try
        {
            var cards = (ListBox)manager.FindName("Cards");
            var trash = Descendants(manager).OfType<RadioButton>().Single(button => Equals(button.Tag, "trash"));
            trash.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            manager.UpdateLayout();
            Require(cards.Items.Count == 12, "The trash section must show all twelve notes");
            CheckGrid();

            ((Button)manager.FindName("ListViewButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            manager.UpdateLayout();
            var list = Descendants(cards).OfType<ListBoxItem>().ToArray();
            Require(list[0].ActualWidth > cards.ActualWidth * .8 &&
                list[1].TranslatePoint(new Point(), cards).Y > list[0].TranslatePoint(new Point(), cards).Y,
                "List mode must show full-width rows");

            ((Button)manager.FindName("GridViewButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            manager.UpdateLayout();
            CheckGrid();
            ((TextBox)manager.FindName("SearchBox")).Text = "Черновик 12";
            manager.UpdateLayout();
            Require(cards.Items.Count == 1 && Descendants(cards).OfType<ListBoxItem>().Single().ActualWidth < cards.ActualWidth * .4,
                "The only matching card must keep its column width");

            var active = new Note { Title = "Заметка вне корзины", Body = "Исходный текст" };
            desk.Store.Save(active);
            ((TextBox)manager.FindName("SearchBox")).Clear();
            var select = (Button)manager.FindName("SelectButton");
            select.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            cards.SelectedItems.Add(cards.Items[0]);
            Require(cards.SelectionMode == SelectionMode.Multiple && cards.SelectedItems.Count == 1,
                "The fixture must enter bulk selection in Trash");
            var all = Descendants(manager).OfType<RadioButton>().Single(button => Equals(button.Tag, "all"));
            all.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            manager.UpdateLayout();
            Require(cards.SelectionMode == SelectionMode.Extended && cards.SelectedItems.Count == 0 &&
                Equals(select.Content, "Выбрать") && !((FrameworkElement)manager.FindName("BulkBar")).IsVisible,
                "Changing sections must clear bulk selection and its command bar");
            cards.SelectedItem = cards.Items[0];
            Require(((FrameworkElement)manager.FindName("ShowButton")).IsVisible, "Normal selection must show the note preview again");
            cards.UnselectAll();
            Require(Descendants(cards).OfType<GroupItem>().Count() == 1 &&
                Descendants(cards).OfType<ListBoxItem>().Single().ActualWidth < cards.ActualWidth * .4,
                "Returning to All notes must restore section grouping without stretching a single card");
            trash.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            manager.UpdateLayout();
            CheckGrid();
            Console.WriteLine("PASS grid stays compact when switching sections, views and narrowing search to one card");

            var conflicts = (RadioButton)manager.FindName("ConflictsSection");
            var search = (TextBox)manager.FindName("SearchBox");
            Require(!conflicts.IsVisible, "An empty conflicts section must be hidden");
            var operation = desk.Store.Pending().Single(op => op.Note.Id == active.Id);
            desk.Store.SetConflict(operation, active with { Revision = 1, Body = "Текст с другого устройства" });
            search.Text = active.Title;
            Require(conflicts.IsVisible, "Conflicting edits must restore the conflicts section");
            conflicts.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(cards.Items.Count == 1 && ((TextBlock)manager.FindName("FilterTitle")).Text == "Конфликты",
                "The conflicts section must show the affected note");
            desk.Store.Resolve(active.Id, "local");
            search.Clear();
            Require(!conflicts.IsVisible && ((TextBlock)manager.FindName("FilterTitle")).Text == "Все заметки",
                "Resolving the last conflict must return the manager to All notes");
            Console.WriteLine("PASS changing sections resets selection, and conflicts navigation appears only when needed");

            var source = PresentationSource.FromVisual(manager)!;
            var fromDevice = source.CompositionTarget!.TransformFromDevice;
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var pointer = new Point(work.Left + 50, work.Top + 60);
            var expected = fromDevice.Transform(pointer) + new Vector(10, 12);
            Require(desk.PlaceOnDesktop(active.Id, pointer, fromDevice), "An active note must be placeable from the manager");
            var placed = desk.Store.GetPlacement(active.Id);
            var window = Application.Current.Windows.OfType<StickyWindow>().Single(w => w.NoteId == active.Id);
            Require(placed.Visible && placed.StackId == null && window.IsVisible && Math.Abs(placed.X - expected.X) < 1 &&
                Math.Abs(placed.Y - expected.Y) < 1 && desk.Store.All().Count == 13 &&
                desk.Store.Find(active.Id)!.Note.Body == active.Body, "Placement must use the drop point and preserve the original note");
            var deleted = desk.Store.All().First(row => row.Note.DeletedAt != null).Note;
            Require(!desk.PlaceOnDesktop(deleted.Id, pointer, fromDevice) && !desk.Store.GetPlacement(deleted.Id).Visible,
                "A note in Trash must never be placed by dragging");
            Console.WriteLine("PASS desktop placement opens the original active note as a loose sheet at the drop location");

            desk.Store.SavePlacement(new Placement { NoteId = deleted.Id, Visible = true });
            trash.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            cards.SelectedItem = cards.Items.OfType<NoteCard>().Single(card => card.Id == deleted.Id);
            ((Button)manager.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(desk.Store.Find(deleted.Id)?.Note.DeletedAt == null && !desk.Store.GetPlacement(deleted.Id).Visible &&
                !Application.Current.Windows.OfType<StickyWindow>().Any(w => w.NoteId == deleted.Id),
                "Restoring a formerly visible sheet must restore it only to the manager");

            search.Text = "Search with no results";
            ((Button)manager.FindName("NewNoteButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(cards.SelectedItem is NoteCard && search.Text.Length == 0 && desk.Store.All().Count == 14,
                "New note must leave Trash and show the new note editor even if search was active");
            var created = ((NoteCard)cards.SelectedItem).Id;
            Require(!desk.Store.GetPlacement(created).Visible && ((TextBox)manager.FindName("EditTitle")).Text == "",
                "A note created in the manager must stay there");

            desk.CreateStack("Стопка 1");
            Require(desk.StackCard(created, window), "A card dropped on a loose sheet must create a stack");
            var stack = desk.Store.GetPlacement(active.Id).StackId ?? throw new InvalidOperationException("Missing desktop stack");
            Require(desk.Store.GetPlacement(created) is { Visible: true } p && p.StackId == stack &&
                desk.Stack(stack).ActiveNoteId == created && desk.StackNotes(stack).Count == 2 && window.NoteId == created &&
                Math.Abs(desk.Stack(stack).X - placed.X) < 1 && Math.Abs(desk.Stack(stack).Y - placed.Y) < 1,
                "The dragged card must become the top sheet, retaining the target position and window");
            Require(desk.StackName(stack) == "Стопка 1", "An empty stack must not reserve the first number");
            Require(desk.StackCard(deleted.Id, window) && desk.StackNotes(stack).Count == 3 &&
                desk.Store.GetPlacement(deleted.Id).StackId == stack && window.NoteId == deleted.Id,
                "A card dropped onto a stack must join that same stack");
            Require(!desk.StackCard(deleted.Id, window), "Dropping a card on itself must not change its stack");

            ((Button)window.FindName("HideButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(desk.Store.Find(deleted.Id)?.Note is { DeletedAt: null, ArchivedAt: null } &&
                !desk.Store.GetPlacement(deleted.Id).Visible && desk.StackNotes(stack).Count == 2,
                "The desktop X must hide a restored sheet without returning it to Trash");
            desk.SelectStackNote(created);
            ((Button)window.FindName("HideButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(desk.Store.Find(created)?.Note is { IsEmpty: true, DeletedAt: null } &&
                !desk.Store.GetPlacement(created).Visible && desk.StackNotes(stack).Count == 1,
                "The desktop X must keep even a blank note in the manager");
            Console.WriteLine("PASS manager creates and restores off-desktop, cards stack, and X only hides sheets");

            void CheckGrid()
            {
                var grid = Descendants(cards).OfType<ListBoxItem>().ToArray();
                var first = grid[0].TranslatePoint(new Point(), cards);
                var second = grid[1].TranslatePoint(new Point(), cards);
                Require(second.X > first.X && Math.Abs(second.Y - first.Y) < 1 && grid[0].ActualWidth < cards.ActualWidth * .4,
                    $"Trash grid must place compact cards side by side (card {grid[0].ActualWidth:F0}, view {cards.ActualWidth:F0})");
            }
        }
        finally { manager.Close(); }
    }

    private static void CheckPictures(string folder)
    {
        var profile = Path.Combine(folder, "pictures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), Json.Write(new Settings { Animations = false }));
        using var desk = new DeskController(profile);
        var note = new Note();
        desk.Store.Save(note);
        var manager = new ManagerWindow(desk) { ShowActivated = false };
        manager.Show();
        try
        {
            var cards = (ListBox)manager.FindName("Cards");
            cards.SelectedItem = cards.Items.OfType<NoteCard>().Single();
            manager.UpdateLayout();
            Require(((ColumnDefinition)manager.FindName("EditorColumn")).ActualWidth > 420 &&
                ((TextBlock)manager.FindName("TitlePlaceholder")).IsVisible && ((TextBlock)manager.FindName("BodyPlaceholder")).IsVisible,
                "The wide editor must show two placeholders for an empty note");

            var pictures = (NotePictures)manager.FindName("EditorPictures");
            var body = (NoteBodyEditor)manager.FindName("EditBody");
            var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, new byte[64], 16);
            Require(pictures.TryAttach(new DataObject(DataFormats.Bitmap, bitmap)), "Paste must accept a Windows bitmap");
            var originalId = desk.Store.ImageIds(note.Id).Single();
            Require(desk.Store.ImageLayouts(note.Id).Single().Layout is { Mode: ImageTextMode.Inline },
                "A pasted picture must use the scrolling document flow by default");
            body.Text = string.Join('\n', Enumerable.Range(1, 80).Select(i => $"Проверочная строка {i}"));
            manager.UpdateLayout();
            var bodyScroll = Descendants(body).OfType<ScrollViewer>().Single(v => v.Content is StackPanel);
            var originalInline = Descendants(body).OfType<Border>().Single(b => Equals(b.Tag, originalId));
            ScrollBody(0);
            var initialTop = originalInline.TranslatePoint(new Point(), body).Y;
            ScrollBody(20);
            Require(originalInline.TranslatePoint(new Point(), body).Y + originalInline.ActualHeight < 0 && initialTop >= 0,
                "An inline picture must leave the viewport when the note scrolls");
            ScrollBody(0);
            ChooseMode(originalInline, "Поверх текста");
            var overlay = pictures.Children.OfType<Border>().Single(b => Equals(b.Tag, originalId));
            var overlayTop = Canvas.GetTop(overlay);
            ScrollBody(20);
            Require(Canvas.GetTop(overlay) + overlay.ActualHeight < 0 && Canvas.GetTop(overlay) < overlayTop,
                "An overlay must move with the document and clip above the viewport");
            ScrollBody(0);

            var png = desk.Store.ReadImage(note.Id, originalId)!;
            var path = Path.Combine(profile, "dropped.png");
            File.WriteAllBytes(path, png);
            Require(pictures.TryAttach(new DataObject(DataFormats.FileDrop, new[] { path })) &&
                desk.Store.ImageIds(note.Id).Count == 2, "File drop must save a second image");
            var virtualFile = new BrowserFileFixture(png);
            Require(pictures.TryAttach(virtualFile, new Point(160, 130)) && virtualFile.ReadNativeStream &&
                desk.Store.ImageLayouts(note.Id) is { Count: 3 } attachments &&
                attachments[^1].Layout != null, "Browser file data must add an image at the drop location");
            manager.UpdateLayout();
            var frame = pictures.Children.OfType<Border>().Single(b => Equals(b.Tag, originalId));
            var thumbs = Descendants(frame).OfType<Thumb>().ToArray();
            Require(thumbs.Length == 2 && frame.ActualWidth > pictures.ActualWidth * .5,
                "The first image must have a large canvas frame with move and resize handles");
            var oldX = Canvas.GetLeft(frame);
            var oldY = Canvas.GetTop(frame);
            var oldWidth = frame.Width;
            thumbs[0].RaiseEvent(new DragDeltaEventArgs(-36, 28) { RoutedEvent = Thumb.DragDeltaEvent });
            thumbs[0].RaiseEvent(new DragCompletedEventArgs(-36, 28, false) { RoutedEvent = Thumb.DragCompletedEvent });
            thumbs[1].RaiseEvent(new DragDeltaEventArgs(-42, -42) { RoutedEvent = Thumb.DragDeltaEvent });
            thumbs[1].RaiseEvent(new DragCompletedEventArgs(-42, -42, false) { RoutedEvent = Thumb.DragCompletedEvent });
            var placed = desk.Store.ImageLayouts(note.Id).Single(p => p.Id == originalId).Layout!;
            Require(Canvas.GetLeft(frame) < oldX && Canvas.GetTop(frame) > oldY && frame.Width < oldWidth &&
                Math.Abs(frame.Width - frame.Height) < 1, "Dragging must move and resize the image without changing its aspect ratio");
            frame.ContextMenu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var preview = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Изображение");
            Require(preview.IsVisible, "The image must open the full viewer");
            preview.Close();

            desk.ShowSticky(note.Id);
            var sheet = Application.Current.Windows.OfType<StickyWindow>().Single(w => w.NoteId == note.Id);
            sheet.UpdateLayout();
            Require(((NotePictures)sheet.FindName("Pictures")).Children.Count +
                Descendants((NoteBodyEditor)sheet.FindName("BodyBox")).OfType<Border>().Count(b => b.Tag is Guid) == 3,
                "Desktop sheets must show the same overlay and inline images");
            var backup = desk.Store.Export();
            using var imported = new NoteStore(Path.Combine(profile, "imported.db"));
            imported.Import(backup);
            Require(imported.ImageLayouts(note.Id).Count == 3 && imported.ImageLayouts(note.Id)[0].Layout == placed && imported.ImageIds(note.Id)
                .All(id => imported.ReadImage(note.Id, id)!.SequenceEqual(png)), "JSON backup must carry the pixels");
            using var reopened = new NoteStore(Path.Combine(profile, "profiles", "local", "notes.db"));
            Require(reopened.ReadImage(note.Id, originalId)!.SequenceEqual(png) &&
                reopened.ImageLayouts(note.Id)[0].Layout == placed, "The picture and layout must survive reopening the database");
            var remove = frame.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Удалить изображение"));
            remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(desk.Store.ImageIds(note.Id).Count == 2, "Removing one picture must leave the other pictures and note");

            body.Text = "| План | Срок |\n| --- | --- |\n| Встреча | Завтра |\nТекст после таблицы";
            var cardsPreview = new ManagerViewModel(desk);
            cardsPreview.Refresh();
            Require(cardsPreview.Notes.Single().Preview == "▦  План · Срок\nТекст после таблицы",
                "A card must summarize the table without pipe markup and retain surrounding text");
            body.Text = "Список дел\n" + string.Join(' ', Enumerable.Repeat("Длинный абзац с задачами на неделю.", 12));
            manager.UpdateLayout();
            var editableId = desk.Store.ImageIds(note.Id)[0];
            ChooseMode(Descendants(body).OfType<Border>().Single(b => Equals(b.Tag, editableId)), "Поверх текста");
            ChooseMode(pictures.Children.OfType<Border>().Single(b => Equals(b.Tag, editableId)), "В тексте");
            var inline = Descendants(body).OfType<Border>().Single(b => Equals(b.Tag, editableId));
            Require(inline.Parent is Grid { ColumnDefinitions.Count: 0 } && !pictures.Children.Contains(inline),
                "An inline image must sit in a scrolling row of the body");
            ChooseMode(inline, "Обтекание");
            var wrap = desk.Store.ImageLayouts(note.Id).Single(p => p.Id == editableId).Layout!;
            desk.Store.SaveImageLayout(note.Id, editableId, wrap with { Anchor = 1, X = .1, Width = .42 });
            var otherPicture = desk.Store.ImageLayouts(note.Id).Single(p => p.Id != editableId);
            desk.Store.SaveImageLayout(note.Id, otherPicture.Id, otherPicture.Layout! with { Anchor = 1, Mode = ImageTextMode.Inline });
            pictures.Bind(desk.Store, note.Id, true);
            manager.UpdateLayout();
            var wrapped = Descendants(body).OfType<Border>().Single(b => Equals(b.Tag, editableId));
            Require(wrapped.Parent is Grid { ColumnDefinitions.Count: 2 } region &&
                region.Children.OfType<StackPanel>().Single().Children.Count > 0, "Text must use rows beside a wrapped image");
            SaveSized((FrameworkElement)manager.Content, Path.Combine(folder, "wrapped-image.png"));
            body.Text = "Новая строка\n" + body.Text;
            Require(desk.Store.ImageLayouts(note.Id).Single(p => p.Id == editableId).Layout is { Anchor: 2, Mode: ImageTextMode.Wrap },
                "A wrapped image must follow its paragraph when a line is inserted earlier");
            ChooseMode(wrapped, "Поверх текста");
            Require(pictures.Children.OfType<Border>().Any(b => Equals(b.Tag, editableId)),
                "An overlay image must return to the freely positioned canvas");
            desk.ChangeState(note.Id, "trash", animateSticky: false);
            desk.PurgeTrash([note.Id]).GetAwaiter().GetResult();
            Require(reopened.Find(note.Id) == null && reopened.ImageIds(note.Id).Count == 0,
                "Permanent deletion must remove all local images with the note");

            void ChooseMode(Border picture, string label)
            {
                var id = picture.Tag;
                pictures.Bind(desk.Store, note.Id, true);
                manager.UpdateLayout();
                picture = pictures.Children.OfType<Border>().Concat(Descendants(body).OfType<Border>()).Single(b => Equals(b.Tag, id));
                picture.BringIntoView();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var menu = picture.ContextMenu;
                menu.PlacementTarget = picture;
                menu.IsOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var placement = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Размещение изображения"));
                var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(placement);
                ((System.Windows.Automation.Provider.IExpandCollapseProvider)peer.GetPattern(
                    System.Windows.Automation.Peers.PatternInterface.ExpandCollapse)).Expand();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var choice = placement.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label));
                Require(placement.Template.FindName("PART_Popup", placement) is Popup { IsOpen: true } && choice.IsVisible,
                    $"The live image submenu must show {label}, menu={menu.IsOpen}, submenu={placement.IsSubmenuOpen}, choice={choice.IsVisible}");
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
                manager.UpdateLayout();
            }

            void ScrollBody(double offset)
            {
                bodyScroll.ScrollToVerticalOffset(offset);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                manager.UpdateLayout();
            }
        }
        finally { manager.Close(); }
        Console.WriteLine("PASS image drop, move and resize, text placement modes, preview, JSON backup and deletion");
    }

    private static void CheckDesktopStacks(string folder)
    {
        var profile = Path.Combine(folder, "desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), Json.Write(new Settings { Animations = false }));
        var a = new Note { Title = "План релиза", Body = "☐ Записать демо" };
        var b = new Note { Title = "Проверка", Body = "☐ Собрать Windows-версию" };
        var c = new Note { Title = "Молоко", Body = "Список покупок" };
        var d = new Note { Title = "Напоминание", Body = "Позвонить в пятницу" };
        Guid work, home;
        Rect movedBounds;
        using (var desk = new DeskController(profile))
        {
            foreach (var note in new[] { a, b, c, d })
            {
                desk.Store.Save(note);
                desk.Store.SavePlacement(new Placement { NoteId = note.Id, Visible = true });
            }
            desk.Start();
            Require(Windows().Count == 1, "Existing visible notes must still appear in the main stack");
            CheckSettings(desk);

            var manager = new ManagerWindow(desk);
            manager.Show();
            try
            {
                Click((Button)manager.FindName("SelectButton"));
                var cards = (ListBox)manager.FindName("Cards");
                cards.SelectedItems.Add(cards.Items.Cast<PaperDesk.Desktop.ViewModels.NoteCard>().Single(card => card.Id == a.Id));
                cards.SelectedItems.Add(cards.Items.Cast<PaperDesk.Desktop.ViewModels.NoteCard>().Single(card => card.Id == b.Id));
                var assign = (Button)manager.FindName("BulkStack");
                Require(assign.IsVisible, "Multi-selection must expose the stack action");
                Click(assign);
                var menu = OpenMenu();
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Новая стопка");
                    dialog.UpdateLayout();
                    Descendants(dialog).OfType<TextBox>().Single().Text = "Работа";
                    Click(Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Создать")));
                }, DispatcherPriority.ContextIdle);
                Pick(menu, "Новая стопка…");
                work = desk.Store.AllStacks().Single(s => s.Name == "Работа").Id;
                Require(desk.StackNotes(work).Select(n => n.Id).ToHashSet().SetEquals([a.Id, b.Id]), "Creating a stack must move both selected notes");

                home = desk.CreateStack("Дом");
                desk.AssignStack([c.Id], home);
                desk.AssignStack([d.Id], null);
                Require(Windows().Count == 3 && Windows().Count(w => w.StackId == null) == 1,
                    "Two named stacks and a loose note must coexist");
                var workWindow = Windows().Single(w => w.StackId == work);
                var homeWindow = Windows().Single(w => w.StackId == home);
                var workHandle = new WindowInteropHelper(workWindow).Handle;
                var homeHandle = new WindowInteropHelper(homeWindow).Handle;
                var original = workWindow.NoteId;
                Click((Button)workWindow.FindName("NextSheet"));
                Require(workWindow.NoteId != original && new WindowInteropHelper(workWindow).Handle == workHandle &&
                    homeWindow.NoteId == c.Id && new WindowInteropHelper(homeWindow).Handle == homeHandle,
                    "Turning one stack must reuse its native window and leave the other stack alone");
                Require(((Button)homeWindow.FindName("StackLabel")).IsVisible &&
                    ((FrameworkElement)homeWindow.FindName("StackBack1")).Visibility == Visibility.Collapsed,
                    "A named one-note stack must show its name without a fictitious backing sheet");
                Require(!new Rect(workWindow.Left, workWindow.Top, workWindow.Width, workWindow.Height)
                    .IntersectsWith(new Rect(homeWindow.Left, homeWindow.Top, homeWindow.Width, homeWindow.Height)),
                    "New stacks should start apart when there is room");
                workWindow.Left += 14; workWindow.Top += 11; workWindow.Width -= 20;
                movedBounds = new Rect(workWindow.Left, workWindow.Top, workWindow.Width, workWindow.Height);
                Click((Button)workWindow.FindName("MinimizeButton"));
                Require(!workWindow.IsVisible && homeWindow.IsVisible, "Minimizing one stack must not hide its neighbors");
                manager.UpdateLayout();
                var sections = Descendants(cards).OfType<GroupItem>().ToArray();
                Require(sections.Length == 2 &&
                    Descendants(sections[0]).OfType<TextBlock>().Any(label => label.IsVisible && label.Text == "На столе") &&
                    Descendants(sections[1]).OfType<TextBlock>().Any(label => label.IsVisible && label.Text == "Не на столе") &&
                    sections[1].TranslatePoint(new Point(), cards).Y > sections[0].TranslatePoint(new Point(), cards).Y,
                    "All notes must render full-width sections with desktop notes above off-desktop notes");
                var gridCards = Descendants(sections[0]).OfType<ListBoxItem>().ToArray();
                Require(gridCards[1].TranslatePoint(new Point(), cards).X > gridCards[0].TranslatePoint(new Point(), cards).X,
                    "The grid must place cards next to each other inside each section");
                var onCard = cards.Items.OfType<PaperDesk.Desktop.ViewModels.NoteCard>().Single(card => card.Id == c.Id);
                var offCard = cards.Items.OfType<PaperDesk.Desktop.ViewModels.NoteCard>().Single(card => card.Id == a.Id);
                Require(onCard.OnDesktop && onCard.StackName == "Дом" && !offCard.OnDesktop && !offCard.HasStack &&
                    Descendants(sections[0]).OfType<TextBlock>().Any(label => label.IsVisible && label.Text == "Дом"),
                    "The open stack must have a readable badge, and a minimized stack must be off desktop");
                Click((Button)manager.FindName("ListViewButton"));
                manager.UpdateLayout();
                Require(Descendants(cards).OfType<GroupItem>().Count() == 2, "List view must keep both sections");
                Click((Button)manager.FindName("GridViewButton"));
                var stacksButton = (Button)manager.FindName("DesktopStacksButton");
                Click(stacksButton); Pick(OpenMenu(), "Работа");
                Require(workWindow.IsVisible && homeWindow.IsVisible, "The manager must restore the chosen stack");
                desk.CloseStack(work);
                Require(Windows().Count == 2 && homeWindow.IsVisible && !desk.Store.GetPlacement(a.Id).Visible &&
                    desk.Store.GetPlacement(c.Id).Visible, "Closing a stack must preserve and hide only its own notes");
                var stale = desk.CreateStack("Пустая стопка");
                var discarded = new Note { Title = "Последний листик" };
                desk.Store.Save(discarded);
                desk.Store.SavePlacement(new Placement { NoteId = discarded.Id, StackId = stale });
                desk.ChangeState(discarded.Id, "trash");
                Click(stacksButton);
                var stackMenu = OpenMenu();
                var stackNames = stackMenu.Items.OfType<MenuItem>().Select(item => ((TextBlock)item.Header).Text).ToArray();
                Require(stackNames.Length == 2 && stackNames.Contains("Дом") && stackNames.Contains("Работа"),
                    "The stack menu must omit empty stacks and retain the closed stack with live notes");
                Pick(stackMenu, "Работа");
                Require(desk.StackNotes(work).Count == 2 && Windows().Count == 3, "A closed stack must reopen as a group");
                Require(desk.Store.Find(a.Id)!.Note.Body == a.Body, "Grouping must not modify the note body");
            }
            finally { manager.Close(); }
        }
        using (var reopened = new DeskController(profile))
        {
            reopened.Start();
            var workWindow = Windows().Single(w => w.StackId == work);
            Require(Windows().Count == 3 && Windows().Single(w => w.StackId == home).NoteId == c.Id &&
                new Rect(workWindow.Left, workWindow.Top, workWindow.Width, workWindow.Height) == movedBounds && workWindow.IsVisible,
                "Membership and independent geometry must survive relaunch");
            Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.Background);
            workWindow.UpdateLayout();
            SaveSized((FrameworkElement)workWindow.Content, Path.Combine(folder, "named-stack.png"));
            var homeWindow = Windows().Single(w => w.StackId == home);
            var removed = workWindow.NoteId;
            reopened.ChangeState(removed, "trash");
            Require(workWindow.NoteId != removed && homeWindow.NoteId == c.Id && homeWindow.IsVisible &&
                reopened.Store.Find(removed)!.Note.DeletedAt.HasValue,
                "Trashing a top note must reveal the next sheet from the same stack");
        }
        Console.WriteLine("PASS manager sections, stack badges and menus reflect visible, minimized, closed and empty stacks");

        void CheckSettings(DeskController desk)
        {
            var settings = new SettingsWindow(desk) { ShowActivated = false };
            settings.Show();
            try
            {
                Require(!Descendants(settings).OfType<Expander>().Any() &&
                    Descendants(settings).OfType<TextBlock>().Any(text => text.Text == "Заметки и изображения хранятся на этом компьютере."),
                    "The local edition must omit cloud setup and show where notes are stored");
            }
            finally { settings.Close(); }
            Console.WriteLine("PASS local settings omit account and cloud setup");
        }
        static List<StickyWindow> Windows() => Application.Current.Windows.OfType<StickyWindow>().ToList();
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static ContextMenu OpenMenu()
        {
            Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.Background);
            return PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Select(source => source.RootVisual).OfType<Visual>().SelectMany(Descendants)
                .OfType<ContextMenu>().Single(menu => menu.IsOpen);
        }
        static void Pick(ContextMenu menu, string label)
        {
            var item = menu.Items.OfType<MenuItem>().Single(item => (item.Header as TextBlock)?.Text == label);
            menu.IsOpen = false;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
    }
    static void SaveSized(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static void CheckPeelAndDrop(string folder)
    {
        var profile = Path.Combine(folder, "peel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), Json.Write(new Settings { Animations = false }));
        using var desk = new DeskController(profile);
        var notes = Enumerable.Range(0, 3).Select(index => new Note
            { Title = $"Sheet {index + 1}", Body = "Keep this text", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index) }).ToArray();
        foreach (var note in notes)
        {
            desk.Store.Save(note);
            desk.Store.SavePlacement(new Placement { NoteId = note.Id, Visible = true });
        }
        desk.Start();
        var source = Sheets().Single();
        var origin = Bounds(source);
        var body = (NoteBodyEditor)source.FindName("BodyBox");
        var format = (Button)source.FindName("FormatButton");
        Settle();
        var topHandle = (FrameworkElement)source.FindName("DragHandle");
        var bottomHandle = (FrameworkElement)source.FindName("PeelHandle");
        var resize = (FrameworkElement)source.FindName("ResizeCorner");
        Require(bottomHandle.IsVisible && bottomHandle.PointToScreen(new Point(0, 0)).Y >
            topHandle.PointToScreen(new Point(0, topHandle.ActualHeight)).Y &&
            bottomHandle.PointToScreen(new Point(bottomHandle.ActualWidth, 0)).X < resize.PointToScreen(new Point()).X,
            "The peel grip must sit below the move handle and leave the resize corner free");
        Require(!format.IsVisible, "A hanging sheet must not show the formatting button");
        body.BeginEdit();
        Settle();
        Require(format.IsVisible, "Editing must expose formatting");
        body.EndEdit();
        Settle();
        Require(!format.IsVisible, "Finishing editing must hide formatting again");

        source.DragStarted(wholeStack: false);
        Settle();
        var under = Sheets().Single(w => w != source);
        Require(under.NoteId == notes[1].Id && Bounds(under) == origin,
            "The real next note must remain under the dragged sheet before release");
        Require(desk.Store.GetPlacement(source.NoteId).StackId == Guid.Empty, "A pending drag must leave stored membership untouched");
        source.DragFinished(null, origin);
        Require(Sheets().Single() == source && desk.StackNotes(Guid.Empty).Count == 3,
            "Cancelling the peel must return the sheet to its source stack");

        source.DragStarted(wholeStack: false);
        source.Left = origin.Right + 42;
        source.Top += 28;
        Settle();
        var looseBounds = Bounds(source);
        var emptyPoint = Center(source.PaperScreenBounds());
        source.DragFinished(emptyPoint, origin);
        Require(source.StackId == null && desk.Store.GetPlacement(source.NoteId).StackId == null &&
            Bounds(source) == looseBounds && desk.StackNotes(Guid.Empty).Count == 2,
            "Dropping on empty space must leave a loose sheet in place");

        var second = Sheets().Single(w => w.StackId == Guid.Empty);
        second.DragStarted(wholeStack: false);
        Settle();
        var dropPoint = Center(source.PaperScreenBounds());
        second.DragMoved(dropPoint, default);
        Require(((FrameworkElement)source.FindName("DropOutline")).IsVisible, "The destination sheet must highlight before drop");
        second.DragFinished(dropPoint, origin);
        Require(second.StackId is Guid group && group != Guid.Empty && desk.StackNotes(group).Count == 2 &&
            Bounds(second) == looseBounds && desk.StackNotes(Guid.Empty).Count == 1 &&
            desk.Store.GetStack(group)?.ActiveNoteId == second.NoteId && Sheets().Count == 2,
            "Dropping onto a loose sheet must create a second stack at its bounds with the dragged sheet on top");
        var stackId = second.StackId!.Value;
        var stackLabel = (Button)second.FindName("StackLabel");
        var picker = StickyTools.CreateStackPicker(desk, second.NoteId, stackLabel);
        picker.IsOpen = true;
        Settle();
        var rename = Descendants(picker.Child).OfType<Button>()
            .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Переименовать стопку");
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "Переименовать стопку");
            dialog.UpdateLayout();
            var input = Descendants(dialog).OfType<TextBox>().Single();
            Require(input.Text == desk.StackName(stackId), "Rename must start with the current name");
            input.Text = "Рабочие идеи";
            Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Сохранить"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }, DispatcherPriority.ContextIdle);
        rename.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(desk.Store.GetStack(stackId)!.Name == "Рабочие идеи" &&
            ((DockPanel)stackLabel.Content).Children.OfType<TextBlock>().Any(label => label.Text == "Рабочие идеи"),
            "Renaming must save to SQLite and update the visible stack label immediately");
        Require(notes.All(note => desk.Store.Find(note.Id)!.Note.Body == note.Body), "Dragging must preserve note content");
        var secondary = Sheets().Single(w => w != second);
        var primaryBounds = Bounds(secondary);
        second.DragStarted(wholeStack: true);
        second.Left += 18;
        second.DragFinished(Center(second.PaperScreenBounds()), looseBounds);
        Require(Bounds(secondary) == primaryBounds && desk.StackNotes(second.StackId!.Value).Count == 2 &&
            Math.Abs(desk.Store.GetStack(second.StackId.Value)!.X - second.Left) < .5,
            "Dragging the top grip must move the whole stack without changing its membership or the other stack");
        Console.WriteLine("PASS editing-only format, separate move and peel grips, stacking and stack renaming");

        var hidden = new Note { Title = "Hidden sheet in the source stack" };
        desk.Store.Save(hidden);
        desk.Store.SavePlacement(new Placement { NoteId = hidden.Id, StackId = stackId, Visible = false });
        var movedBounds = Bounds(second);
        var targetPoint = Center(secondary.PaperScreenBounds());
        second.DragStarted(wholeStack: true);
        second.DragMoved(targetPoint, default);
        Require(((FrameworkElement)secondary.FindName("DropOutline")).IsVisible,
            "The top grip must highlight a drop target just like the lower grip");
        second.DragFinished(null, movedBounds);
        Require(Bounds(second) == movedBounds && desk.StackNotes(stackId).Count == 2 &&
            !((FrameworkElement)secondary.FindName("DropOutline")).IsVisible,
            "Cancelling a top-grip drop must preserve both stacks and clear the highlight");

        second.DragStarted(wholeStack: true);
        second.Left = secondary.Left;
        second.Top = secondary.Top;
        second.DragFinished(targetPoint, movedBounds);
        Require(Sheets().Single() == second && desk.StackNotes(Guid.Empty).Count == 3 &&
            desk.Store.GetStack(Guid.Empty)?.ActiveNoteId == second.NoteId &&
            desk.Store.GetPlacement(hidden.Id) is { Visible: false, StackId: Guid hiddenGroup } && hiddenGroup == Guid.Empty,
            "Dropping the top grip must merge every member of the source stack while keeping hidden sheets hidden");

        for (var attempt = 0; attempt < 12; attempt++)
        {
            second.DragStarted(wholeStack: false);
            second.Left = looseBounds.Left;
            second.Top = looseBounds.Top;
            Settle();
            second.DragFinished(Center(second.PaperScreenBounds()), primaryBounds);
            Require(second.StackId == null && desk.StackNotes(Guid.Empty).Count == 2,
                "The lower grip must still peel one sheet from a merged stack");
            var other = Sheets().Single(w => w != second);
            second.DragStarted(wholeStack: true);
            second.DragFinished(Center(other.PaperScreenBounds()), looseBounds);
            Require(second.StackId == Guid.Empty && desk.StackNotes(Guid.Empty).Count == 3,
                "A loose sheet must also join a stack when dragged by its top grip");
        }
        Console.WriteLine("PASS top-grip merge, cancellation and 12 consecutive peel and rejoin cycles preserve membership");

        static List<StickyWindow> Sheets() => Application.Current.Windows.OfType<StickyWindow>().Where(w => w.IsVisible).ToList();
        static Rect Bounds(StickyWindow w) => new(w.Left, w.Top, w.Width, w.Height);
        static Point Center(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        static void Settle() => Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.Background);
    }

    static void Layout(FrameworkElement e){e.Measure(new Size(e.Width,e.Height));e.Arrange(new Rect(0,0,e.Width,e.Height));e.UpdateLayout();}
    static void SettleLayout(FrameworkElement element)
    {
        Layout(element);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => {}, System.Windows.Threading.DispatcherPriority.Background);
        Layout(element);
    }
    static void RequireWholeRows(StackPanel panel)
    {
        var clip = panel.Clip.Bounds;
        var shown = panel.Children.OfType<FrameworkElement>()
            .Select(row => row.TransformToAncestor(panel).TransformBounds(new Rect(row.RenderSize)))
            .Where(bounds => bounds.Bottom > clip.Top + .5 && bounds.Top < clip.Bottom - .5).ToList();
        Require(shown.Count > 0 && shown.All(bounds => bounds.Top >= clip.Top - .5 && bounds.Bottom <= clip.Bottom + .5),
            "Only whole rows should intersect the viewport at either edge");
    }
    static byte CornerAlpha(PaperSurface paper)
    {
        return PixelAlpha(Render(paper), new Int32Rect((int)paper.Width - 10, (int)paper.Height - 10, 1, 1));
    }
    private static void CheckFormatting(string folder)
    {
        var body = new NoteBodyEditor { Width = 380, Height = 360, FontSize = 16 };
        var format = new Button { Content = "Формат" };
        format.Click += (_, _) => body.OpenFormatMenu(format);
        var panel = new DockPanel();
        DockPanel.SetDock(format, Dock.Top);
        panel.Children.Add(format);
        panel.Children.Add(body);
        var host = new Window { Content = panel, Width = 430, Height = 430, ShowInTaskbar = false };
        host.Show();
        try
        {
            body.Text = "- Первый\n- [x] Второй";
            body.BeginEdit();
            Field().CaretIndex = 0;
            SendKey(Key.Back);
            Require(body.Text == "- Первый\nВторой", "Backspace at the start of a checked task must remove the box and keep its label");
            body.EndEdit();
            Edit("Первый").CaretIndex = 0;
            SendKey(Key.Back);
            Require(body.Text == "Первый\nВторой", "Backspace at the beginning of a bullet must turn it into plain text");
            body.EndEdit();
            body.Text = "1. Номер\n- ";
            body.BeginEdit();
            SendKey(Key.Back);
            Require(body.Text == "1. Номер\n", "Backspace on an empty bullet must leave an ordinary blank line");
            body.EndEdit();
            body.Text = "Текст\n- [ ] ";
            body.BeginEdit();
            SendKey(Key.Back);
            Require(body.Text == "Текст\n", "Backspace must also remove an empty checkbox");
            Console.WriteLine("PASS Backspace removes bullet and checklist markers without deleting text");

            body.EndEdit();
            body.Text = "- Первый\n- [x] Второй\nСтрока";
            body.BeginEdit();
            ApplicationCommands.SelectAll.Execute(null, Field());
            Require(Descendants(body).OfType<TextBlock>().Count(t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(run => run.Background != null)) == 2,
                "Select All from the active row must highlight the other rows");
            ChooseFormat("Жирный");
            Require(body.Text == "- **Первый**\n- [x] **Второй**\n**Строка**", "Format must use the selection across list rows and preserve their markers");
            body.EndEdit();
            Edit("Второй").SelectAll();
            ChooseFormat("Курсив");
            var styled = new PaperInline(body.Text.Split('\n')[1]);
            Require(styled.Text == "- [x] Второй" && styled.HasStyle(6, styled.Text.Length, PaperInline.Style.Bold | PaperInline.Style.Italic),
                "Combining bold and italic must preserve the existing style and the selected label");
            Field().Text = "Правка";
            body.EndEdit();
            Edit("Правка");
            Require(Runs().Single() is { FontWeight: var weight, FontStyle: var style } &&
                weight == FontWeights.Bold && style == FontStyles.Italic,
                "A uniformly formatted selection must keep its appearance when editing it again");
            body.EndEdit();
            body.Text = "Обычный **жирный** и _курсив_";
            Edit("Обычный жирный и курсив");
            var mixed = Runs().ToArray();
            Require(mixed.Length == 4 && mixed[1].Text == "жирный" && mixed[1].FontWeight == FontWeights.Bold &&
                mixed[3].Text == "курсив" && mixed[3].FontStyle == FontStyles.Italic, "Mixed styles must stay visible while editing");
            Pump();
            SaveSized(body, Path.Combine(folder, "mixed-format-editing.png"));
            body.EndEdit();

            foreach (var marker in new[] { "**", "*", "_", "~~", "\x60" })
            {
                var original = marker + "слово" + marker;
                body.Text = original;
                var endField = Edit("слово");
                endField.CaretIndex = endField.Text.Length;
                SendKey(Key.Enter);
                Require(body.Text == original + "\n",
                    $"Enter after {marker} must keep delimiters on the original line: {Json.Write(body.Text)}");
                body.EndEdit();
            }
            body.Text = "**слово**";
            var formatted = Edit("слово");
            formatted.CaretIndex = 2;
            SendKey(Key.Enter);
            Require(body.Text == "**сл**\n**ово**", "Enter inside formatted text must split both styled halves");
            body.EndEdit();
            body.Text = "**слово**";
            formatted = Edit("слово");
            formatted.Select(1, 2);
            formatted.SelectedText = "X";
            Require(body.Text == "**сXво**", $"Replacing a selection must preserve its inline style: {body.Text}");
            body.EndEdit();
            body.Text = "- **слово**";
            var listItem = Edit("слово");
            listItem.CaretIndex = listItem.Text.Length;
            SendKey(Key.Enter);
            Require(body.Text == "- **слово**\n- ", "Enter must continue a list even when the label ends in a style");
            body.EndEdit();
            Console.WriteLine("PASS Enter and replacement preserve inline Markdown styles");

            body.Text = "До\nСтрока";
            body.BeginEdit();
            PlainField().Select(3, 6);
            ChooseFormat("¶  Подзаголовок");
            Require(body.Text == "До\n## Строка", "Moving focus from selection to Format must keep the selected paragraph");
            Console.WriteLine("PASS document selection, heading and combined inline formatting survive the Format menu");

            body.EndEdit();
            body.Text = "";
            body.BeginEdit();
            ChooseFormat("Код");
            Require(body.Text.Length == 0, "The Code command must wait for the first typed character");
            var input = new TextComposition(InputManager.Current, PlainField(), "x");
            PlainField().RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, input)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            Require(body.Text == "\x60x\x60" && Runs().Single().FontFamily.Source == "Consolas", "Typing after Code must style real input");
            Field().Text = "x + 1";
            body.EndEdit();
            Require(body.Text == "\x60x + 1\x60", "Typing more code must preserve the inline style");
            Console.WriteLine("PASS Code styles typed text without inserting a sample");

            body.Text = "До";
            body.BeginEdit();
            ChooseFormat("▸  Спойлер");
            Require(Field().Text == "" && body.Text == "До\n:::spoiler\n\n:::\n", "New spoilers must start open with an empty body");
            Field().Text = "Секрет";
            Disclosure("▾").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Require(body.Text.Contains("\nСекрет\n", StringComparison.Ordinal) &&
                Descendants(body).OfType<TextBlock>().All(t => t.Text != "Секрет"), "Closing a spoiler while editing must keep and hide its text");
            Edit("Спойлер").Text = "Мой спойлер";
            body.EndEdit();
            Pump();
            Disclosure("▸").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Require(body.Text.Contains(":::spoiler Мой спойлер\nСекрет", StringComparison.Ordinal) &&
                Descendants(body).OfType<TextBlock>().Any(t => t.Text == "Секрет"), "The new title and body must survive reopening a spoiler");
            Console.WriteLine("PASS empty spoiler, rename and close/reopen keep saved content");

            body.EndEdit();
            body.Text = ":::spoiler Заголовок\nСекрет\n:::\nПосле";
            Edit("Заголовок").CaretIndex = 0;
            SendKey(Key.Back);
            Require(body.Text == "Заголовок\nСекрет\nПосле", "Backspace at a spoiler title start must unwrap and preserve its contents");
            body.EndEdit();
            body.Text = ":::spoiler Заголовок\r\n**Секрет**\r\n:::\r\nПосле";
            var spoilerTitle = Edit("Заголовок");
            spoilerTitle.CaretIndex = 2;
            SendKey(Key.Enter);
            Require(Field().Text == "Секрет", "Enter in a collapsed spoiler title must move into its body without splitting the title");
            Field().CaretIndex = 3;
            SendKey(Key.Enter);
            Require(body.Text == ":::spoiler Заголовок\r\n**Сек**\r\n**рет**\r\n:::\r\nПосле",
                "Enter in a formatted spoiler body must keep its closing line and CRLF");
            body.EndEdit();
            Edit("Заголовок").CaretIndex = 0;
            SendKey(Key.Back);
            Require(body.Text == "Заголовок\r\n**Сек**\r\n**рет**\r\nПосле", "Unwrap must preserve existing CRLF and body formatting");
            body.EndEdit();
            body.Text = ":::spoiler\n\n:::";
            Edit("Спойлер").CaretIndex = 0;
            SendKey(Key.Back);
            Require(body.Text == "", "Backspace on an empty spoiler must remove the empty block");
            Console.WriteLine("PASS spoiler title Backspace unwraps safely and removes empty blocks");

            foreach (var existing in new[] { ":::spoiler Заголовок\n\n:::", ":::spoiler Заголовок\n:::" })
            {
                body.EndEdit();
                body.Text = existing;
                Pump();
                Disclosure("▸").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                var prompt = Descendants(body).OfType<Button>().SingleOrDefault(b => Equals(b.Content, "Написать внутри…"));
                if (prompt is { IsVisible: true }) prompt.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                else Edit("Написать внутри…");
                Pump();
                Field().Text = "Внутри блока";
                Require(body.Text == ":::spoiler Заголовок\nВнутри блока\n:::", "The prompt must edit the spoiler body, not the title");
            }
            Console.WriteLine("PASS opening an existing empty spoiler offers and saves body input");

            body.EndEdit();
            body.Text = "| Первый | Второй |\n| --- | --- |\n| 1 | 2 |";
            Pump();
            var secondCell = (Border)Descendants(body).OfType<TextBlock>().Single(t => CaptionText(t) == "Второй").Parent;
            var tableScroller = Descendants(body).OfType<ScrollViewer>().Single(v => v.Content is StackPanel);
            tableScroller.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = secondCell });
            Require(Field().Text == "Второй", "Clicking in a cell's padding must edit that cell");
            body.EndEdit();

            body.EndEdit();
            body.Text = "| А | Б |\n| --- | --- |\n| В | Г |";
            Edit("А").Text = "Более длинный заголовок";
            Edit("Г").Text = "Обновлённая ячейка";
            Require(body.Text == "| Более длинный заголовок | Б |\n| --- | --- |\n| В | Обновлённая ячейка |",
                "Switching cells after a length change must keep the divider and update the clicked cell");
            body.EndEdit();
            body.Text = "| А | Б |\n| --- | --- |\n| В | Г |";
            Edit("А").Text = "А ещё длиннее";
            SendKey(Key.Enter);
            Require(Field().Text == "Б", "Enter after growing a cell must target the actual next cell");
            Field().Text = "Б2";
            Require(body.Text == "| А ещё длиннее | Б2 |\n| --- | --- |\n| В | Г |", "Editing the cell entered via Enter must keep every separator");
            body.EndEdit();
            Edit("Г").CaretIndex = 1;
            SendKey(Key.Enter);
            Require(body.Text.EndsWith("| В | Г |\n", StringComparison.Ordinal) && Field().Text == "",
                "Enter in the last cell must make an editable paragraph after a table at the end of the note");
            Field().Text = "После таблицы";
            Require(body.Text.EndsWith("| В | Г |\nПосле таблицы", StringComparison.Ordinal),
                "Typing after the last cell must not overwrite the table");
            body.EndEdit();
            body.Text = "| А | Б |\n| --- | --- |\n| В | Г |";
            Edit("А");
            body.SelectDocument(body.Text.IndexOf('А'), body.Text.IndexOf('Б') + 1);
            SendKey(Key.Delete);
            Require(body.Text == "|  |  |\n| --- | --- |\n| В | Г |",
                "Deleting a selection across cells must clear values without deleting the table separators");
            body.EndEdit();
            Console.WriteLine("PASS switching between table cells keeps the divider and typed values");

            body.Text = "Вступление\n| А | Б |\n| --- | --- |\n| В | Г |\nИтог";
            Pump();
            TableAction("В", "Добавить строку ниже");
            Require(body.Text.Contains("| В | Г |\n|  |  |\nИтог", StringComparison.Ordinal), "Add row must insert empty cells before the following paragraph");
            Field().Text = "Д";
            body.EndEdit();
            Pump();
            TableAction("Д", "Добавить столбец справа");
            Require(body.Text.Contains("| --- | --- | --- |", StringComparison.Ordinal) &&
                body.Text.Contains("| Д |  |  |", StringComparison.Ordinal), "Add column must extend the header, divider and data rows");
            Field().Text = "Е";
            body.EndEdit();
            Pump();
            TableAction("Е", "Удалить столбец");
            body.EndEdit();
            Pump();
            TableAction("Д", "Удалить строку");
            Require(!body.Text.Contains("Д", StringComparison.Ordinal) && body.Text.Contains("| В | Г |", StringComparison.Ordinal),
                "Delete row and column must preserve other cells");
            body.EndEdit();
            Pump();
            TableAction("В", "Удалить таблицу");
            Require(body.Text == "Вступление\nИтог", "Delete table must keep the paragraphs around it");
            Console.WriteLine("PASS table commands insert and delete rows and columns and remove only the table");
        }
        finally { host.Close(); }

        void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            host.UpdateLayout();
        }
        PaperLineInput Field() => Descendants(body).OfType<PaperLineInput>().Single(t => t.Visibility == Visibility.Visible);
        TextBox PlainField() => Descendants(body).OfType<TextBox>().Single(t => t.Visibility == Visibility.Visible);
        IEnumerable<System.Windows.Documents.Run> Runs() => Field().Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Run>());
        Button Disclosure(string arrow) => Descendants(body).OfType<Button>().Single(b => Equals(b.Content, arrow));
        PaperLineInput Edit(string label)
        {
            Pump();
            var captions = Descendants(body).OfType<TextBlock>().Where(t => t.Tag != null).ToArray();
            var caption = captions.SingleOrDefault(t => CaptionText(t) == label);
            Require(caption != null, $"Expected caption {label}, got [{string.Join(" | ", captions.Select(CaptionText))}]");
            var scroll = Descendants(body).OfType<ScrollViewer>().Single(v => v.Content is StackPanel);
            scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = caption! });
            return Field();
        }
        static string CaptionText(TextBlock caption) =>
            string.Concat(caption.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
        void SendKey(Key key)
        {
            var field = Field();
            field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field)!, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump();
        }
        void ChooseFormat(string label)
        {
            format.Focus();
            format.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Select(source => source.RootVisual).OfType<Visual>().SelectMany(Descendants)
                .OfType<ContextMenu>().Single(m => m.IsOpen);
            Pick(menu, label);
        }
        void TableAction(string value, string label)
        {
            var menu = Edit(value).ContextMenu;
            Require(menu != null, "The table cell needs a context menu while editing");
            Pick(menu!, label);
        }
        void Pick(ContextMenu menu, string label)
        {
            var item = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, label));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Pump();
        }
    }

    static RenderTargetBitmap Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)element.Width, (int)element.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }
    static byte PixelAlpha(RenderTargetBitmap bitmap, Int32Rect location)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(location, pixel, 4, 0);
        return pixel[3];
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);yield return c;foreach(var d in Descendants(c))yield return d;}}
    static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    static double L(Color c){double F(byte b){var x=b/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*F(c.R)+.7152*F(c.G)+.0722*F(c.B);}
    static double Contrast(Color a,Color b){var x=L(a);var y=L(b);return(Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
    static void SaveRender(FrameworkElement element,string path){var b=new RenderTargetBitmap((int)element.Width,(int)element.Height,96,96,PixelFormats.Pbgra32);b.Render(element);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);e.Save(f);}
}
