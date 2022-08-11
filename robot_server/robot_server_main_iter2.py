# Robot server script.

# We use this to read the config file.
from configparser import ConfigParser
# We need this to interact with files
import os
# This is for producing the alert and error messages.
from tkinter import messagebox, filedialog
# This is for concurrency
import threading
# This is for buffer management between threads.
from collections import deque
# Anything network-related stuff comes from here.
import socket
# This is for the GUI
import tkinter as tk
# For timing stuff.
import datetime, time

debug_stuff = True # Turns on a ton of statements to the console.


####################### Functions.

# Timekeeping
def unix_time_now_milliseconds():
    right_now = datetime.datetime.now().timestamp() # This is in microseconds.
    unix_time_ms = round(right_now, 3) # Round to the nearest millisecond
    return unix_time_ms

# We need these functions for the GUI context menu

# Define an alert window here, using tkinter
def config_file_parse_fail_alert():
    messagebox.showerror('Robot Interface server', 'Could not read all the settings from the config file. Delete it, restart this application, and modify the new one as required.')
    quit() # terminate the program

def clear_log():
    global thread_lock
    global log_string_array
    # We grab onto the variable, and will clear it.
    thread_lock.acquire()
    log_string_array = ''
    thread_lock.release()
    # We also clear the contents of the log text area.

def save_logfile():
    global log_string_array

    file_object = filedialog.asksaveasfile(mode='w', defaultextension='txt')
    if file_object == None:
        return
    file_object.write(log_string_array)
    file_object.close()

def copy_pose_to_clipboard():
    # Save the curent pose to the clipboard
    window.clipboard_clear()
    # Make the copied string more MATLAB friendly.
    window.clipboard_append(robot_pose[1:len(robot_pose)]) # get rid of the 'p' thing.

def right_click_event_handler(event):
    try:
        right_click_menu.tk_popup(event.x_root, event.y_root)
    finally:
        right_click_menu.grab_release()


def quit_button_function():
    if(messagebox.askokcancel("Quit?", "Stop the server and quit?")):
        window.quit()

# Our threads.
def udp_server_function():
    # This function is for the UDP server thread.
    time.sleep(1) # Wait for the GUI to load
    global debug_stuff

    # Get all our global variables that we will ever need.
    global tcp_server_ip
    global tcp_port
    global udp_server_ip
    global udp_port
    global robot_busy
    global robot_status
    global robot_force
    global command_fifo
    global log_string_array

    global keep_running

    # This is needed to make sure we don't do access violations
    thread_lock = threading.Lock()


    # For increased responsiveness, I reduced the UDP timeout.
    # So, we re-initialise our socket every X seconds or so.
    while( keep_running == True ):

        # Create the server object
        udp_server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp_server.settimeout(2) # This is in seconds.


        # Bind the IP address to the server
        udp_server.bind((udp_server_ip, udp_port))
        print("UDP server thread started.")

        # We need some flag variables

        reply_expected = False
        reply_string = "OK" # This will be updated.

        while (keep_running == True):
            # Receive data
            try:
                udp_received_data, sender = udp_server.recvfrom(1024)
                udp_received_data = udp_received_data.decode() # Make this a string.

                thread_lock.acquire()
                log_string_array += str(unix_time_now_milliseconds())+ ';\tC->S; ' + udp_received_data + '\n'
                thread_lock.release()

                if(debug_stuff):
                    print('udp_server_function: Received data is:%s'%(udp_received_data))




                # We now implement some very basic communication protocol.

                # Are we busy?
                if(udp_received_data == 'Is the robot busy?'):
                    reply_expected = True
                    if(robot_busy):
                        reply_string = 'The robot is busy.'
                    else:
                        reply_string = 'The robot is not busy.'

                # Are we connected? Does the robot has anything to say?
                if(udp_received_data == 'What is the status of the robot?'):
                    reply_expected = True
                    reply_string = robot_status

                # Get the force value.
                if(udp_received_data == "What is the current force on the robot?"):
                    reply_expected = True
                    reply_string = robot_force

                # Is the robot connected?
                if(udp_received_data == "Is the robot connected?"):
                    reply_expected = True
                    if (robot_status.__contains__("Robot timeout") or robot_status.__contains__("Robot connection lost") or robot_status.__contains__("Robot not connected.") ):
                        reply_string = "No."
                    else:
                        reply_string = "Yes."




                # Try dissecing the string.
                dissected_data = udp_received_data.split(';')
                if(debug_stuff):
                    print('udp_server_function: dissected_data is:', dissected_data)
                    print('udp_server_function: Length of dissected_data_is:', len(dissected_data))



                if(len(dissected_data) == 2):
                    # If we got here, we got a long string with a ; separator. This is our control word.
                    thread_lock.acquire()
                    command_fifo.append((dissected_data[0], dissected_data[1]))
                    thread_lock.release()
                    if(debug_stuff):
                        print('udp_server_function: Control packet received: %s '%(command_fifo))
                        print("udp_server_function: There are currently %d instructions in the buffer."%(len(command_fifo)))


                if(reply_expected):
                    thread_lock.acquire()
                    log_string_array += str(unix_time_now_milliseconds())+ ';\tC<-S; ' + str(reply_string) + '\n'
                    thread_lock.release()
                    # I we got here, send a response back.
                    reply_expected = False
                    reply_string = reply_string.encode('ascii') # Make this into a simple byte array
                    udp_server.sendto(reply_string, sender)

            except:
                pass # ignore timeouts


        # Once we got out of the loop, close the server.
        udp_server.close()

    if(debug_stuff):
            print('udp_server_function: server loop finished, closing down.')
    return


def tcp_server_function():
    time.sleep(1) # Wait for the GUI to load

    global debug_stuff

    # Get all our global variables that we will ever need.
    global tcp_server_ip
    global tcp_port
    global connection_loss_timeout
    global udp_server_ip
    global udp_port
    global robot_busy
    global robot_status
    global robot_force
    global robot_pose
    global command_fifo
    global keep_running
    global log_string_array



    tcp_connected = False

    latest_data_timestamp = 0
    second_latest_data_timestamp = 0


    thread_lock = threading.Lock()

    if(debug_stuff):
        print('TCP server thread started.')

    while keep_running == True:
        while tcp_connected == False:
            # Good old socket stuff.
            tcp_server = socket.socket(socket.AF_INET, socket.SOCK_STREAM) # We use TCP
            tcp_server.bind((tcp_server_ip, tcp_port)) # Define what we connect to
            tcp_server.listen(1) # Wait for connection, no more than 1 connection at once
            tcp_server.settimeout(1) # 1 second timeout.


            try:
                client, client_address = tcp_server.accept()
                print('tcp_server_function: client is:', client)
                if(debug_stuff):
                    print('tcp_server_function: Connection accepted from:', client_address)
                robot_status = 'Robot connected.'
                tcp_connected = True
                second_latest_data_timestamp = unix_time_now_milliseconds() # Measure the time for the first packet to arrive from this point.
            except:
                if(debug_stuff):
                    print('tcp_server_function: waiting for the robot to connect.')
                thread_lock.acquire()
                log_string_array += str(unix_time_now_milliseconds()) + ';\tSRVR;' + ' Waiting for robot connection.' + '\n'
                thread_lock.release()
                if(keep_running == False):
                    # If we need to destroy while we are waiting, this is the way to go.
                    return

        # Note that this is not super-well implemented.

        # These are initial values. We will overwrite them as they go.
        opcode_string = 'freedrive'.encode('ascii')
        argument_string = "OFF".encode('ascii')

        # If we got here, we definitely have a connection.
        thread_lock.acquire()
        log_string_array += str(unix_time_now_milliseconds())+ ';\tSRVR;' + ' Robot connected from address: ' + str(client_address[0]) + ' port:' + str(client_address[1]) + '\n'
        thread_lock.release()


        while (tcp_connected == True):

            # keep waiting for data to come in.
            try:

                # We use select here to detect interruptions in the connection.
                # When the robot encounters an error, it just sends a FIN flag in a TCP packet, and that's it. Then the poor server is waiting forever.


                # Packets should be coming in like crazy, but just in case.
                data = client.recv(1024)

                if(data):
                    # Only execute this when we have data to process.
                    robot_message = data.decode()
                    # Update the time of lates incoming data packet. We check the timeout with respect to this.
                    second_latest_data_timestamp = unix_time_now_milliseconds()

                    if(debug_stuff):
                        # This is great to investigate weird phenomena, but it floods the console.
                        #print("tcp_server_function: Robot says:", robot_message)
                        pass

                    # The robot uses only a handful of messages.
                    # Basically, from this point onwards, it's a bunch of script comparisons
                    # Sometimes, messages tend to jam together, so we need to filter it.

                    # Robot is available
                    if(robot_message.__contains__("I'm not busy.")):
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n" # This one has the new line character in it anyway.
                        thread_lock.release()
                        if(debug_stuff):
                            print("tcp_server_function: The robot is marked as not busy.")
                        robot_busy = False
                        robot_status = 'Robot idle.'

                    # Robot is executing something and won't accept new instructions
                    if(robot_message.__contains__("I am busy.")):
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n" # This one has the new line character in it anyway.
                        robot_busy = True
                        robot_status = 'Robot busy.'
                        robot_force = '0' # When the robot is busy, don't send force data.
                        thread_lock.release()
                        if(debug_stuff):
                            print("tcp_server_function: The robot is marked as busy.")


                    # Robot requests opcode
                    if(robot_message.__contains__("Please tell me what to do.")):
                        # If we got here, we will need to po from the deque object.
                        if(len(command_fifo) >= 1):
                            thread_lock.acquire()
                            log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n" # This one has the new line character in it anyway.
                            thread_lock.release()
                            opcode_string, argument_string = command_fifo.popleft()
                            opcode_string = opcode_string.encode('ascii')
                            argument_string = argument_string.encode('ascii')
                            if(debug_stuff):
                                print("tcp_server_function: Number of instructions waiting in the FIFO:", len(command_fifo))
                                print('tcp_server_function: FIFO contents: %s '%(command_fifo))
                        else:
                            # If we run out of our deque objects, add 'nothing'
                            if not robot_status.__contains__("Freedrive"):
                                # Only update this part of the GUI when the robot is not in freedrive mode.
                                thread_lock.acquire()
                                robot_status = 'Connected (idle)'
                                thread_lock.release()
                            opcode_string = 'nothing'.encode('ascii')
                            argument_string = "".encode('ascii')

                        if(len(command_fifo) >= 1):
                            if(debug_stuff):
                                print("tcp_server_function: Sending opcode", opcode_string, "to the robot.")
                            # Since the robot will be instructed to do nothing a lot of times, only send the log when someting interesting is going on.
                            thread_lock.acquire()
                            log_string_array += str(unix_time_now_milliseconds())+ ';\tS->R; Sending opcode: ' + str(opcode_string) + '\n'
                            thread_lock.release()
                        # Send the response packet
                        client.sendall(opcode_string)

                    # Robot is in freedrive mode.
                    if(robot_message.__contains__("Freedrive mode enabled.")):
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n" # This one has the new line character in it anyway.
                        robot_status = "Freedrive"
                        robot_force = "0" # If in freedrive mode, don't report force data.
                        thread_lock.release()
                        if(debug_stuff):
                            print("tcp_server_function: The robot is in freedrive mode.")
                        #robot_busy = True

                    # Robot sends TCP data, as per URScript
                    if(robot_message.__contains__("p[")):
                        thread_lock.acquire()
                        robot_pose = str(robot_message)
                        thread_lock.release()
                        if(debug_stuff):
                            pass
                            #print("tcp_server_function:", str(robot_message))

                    # Robot sends force data
                    if(robot_message.__contains__("f=") and (robot_message.__contains__("; "))):
                        # If we got here, we need to process the force string.
                        # Example force string: "f=0.7734; " (note the space at the end)
                        # But sometimes, the robot sends nothing.
                        # Let's do some python string bashing.
                        current_string_start_index = robot_message.find("f=")
                        current_string_end_index = robot_message.find("; ")
                        force_number = robot_message[current_string_start_index+2:current_string_end_index-2]
                        """"
                        if(debug_stuff):
                            print('tcp_server_function: force string is: ' + force_number)
                        """
                        if(len(force_number) > 0):
                            # Only update this variable when we have something to update.
                            thread_lock.acquire()
                            robot_force = str(force_number)
                            thread_lock.release()
                        if(debug_stuff):
                            pass
                            #print("tcp_server_function:", str(robot_message))

                    # Robot sends force data
                    if(robot_message.__contains__("Current force on TCP is:")):
                        thread_lock.acquire()
                        robot_force = str(robot_message)
                        thread_lock.release()

                    # Robot can't reach
                    if(robot_message.__contains__("This pose is not reachable.")):
                        thread_lock.acquire()
                        robot_status = "Can't reach required pose."
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n"
                        thread_lock.release()


                    # Robot requests argument for the opcode.
                    if(robot_message.__contains__("Please send argument.")):
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n" # This one has the new line character in it anyway.
                        thread_lock.release()
                        if(debug_stuff):
                            print("tcp_server_function: Sending argument ", argument_string, "to the robot.")
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS->R; Sending argument: ' + str(argument_string) + '\n'
                        thread_lock.release()
                        client.sendall(argument_string)

                    # Robot says invalid argument was given.
                    if(robot_message.__contains__("Invalid argument received")):
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds())+ ';\tS<-R; ' + str(robot_message) + "\n" # This one has the new line character in it anyway.
                        robot_status = "Invalid argument"
                        thread_lock.release()
                else:
                    # If we got here, we are expecting data, but nothing was received.
                    # While short gaps between the packets are OK,
                    # If we get a massive time difference between the packets, then
                    # the robot must have rudely terminated the connection.
                    # This can happen at a safety violation or with the emergency stop.
                    latest_data_timestamp = unix_time_now_milliseconds()
                    # Check if the time difference between the latest and the second latest data.
                    if( latest_data_timestamp - second_latest_data_timestamp >=  connection_loss_timeout ):
                        robot_status = "Robot connection lost"
                        robot_pose = "???"
                        robot_force = "???"
                        # Add this to the log.
                        thread_lock.acquire()
                        log_string_array += str(unix_time_now_milliseconds()) + ';\tSRVR;' + ' No packet was heard from the robot for ' + str(connection_loss_timeout) +' seconds, please reconnect robot.\n'
                        thread_lock.release()
                        tcp_connected = False
                        # Close the connection, so we can open it again.
                        tcp_server.close()

            except:
                # If we got here, we were connected, but there was a timeout.
                latest_data_timestamp = unix_time_now_milliseconds()
                # Check if the time difference between the latest and the second latest data.
                if( latest_data_timestamp - second_latest_data_timestamp >=  connection_loss_timeout ):
                    robot_status = "Robot timeout"
                    robot_pose = "???"
                    robot_force = "???"
                    # Add this to the log.
                    thread_lock.acquire()
                    log_string_array += str(unix_time_now_milliseconds()) + ';\tSRVR;' + ' No packet was heard from the robot for ' + str(connection_loss_timeout) +' seconds, please reconnect robot.\n'
                    thread_lock.release()
                    tcp_connected = False
                    # Close the connection, so we can open it again.
                    tcp_server.close()

                    if(debug_stuff):
                        print("tcp_server_function: no packet was heard in " + str(connection_loss_timeout) + " seconds.\n")
                        # Exit this loop and wait for reconnection
                        break
                # if we received the signal to quit, we quit.
                if(keep_running == False):
                    client.close()
                    return

def refresh_gui_function():
    # It seems that tkinter only updates a GUI string when explicitly modified.
    time.sleep(1) # Wait for the GUI to load

    # These are the variables we are touching
    global keep_running
    global debug_stuff

    if(debug_stuff):
        print('refresh_gui_function: Refresh GUI thread started')

    global robot_status
    global robot_status_string_in_gui

    global robot_pose
    global robot_pose_string_in_gui

    global robot_force
    global robot_force_string_in_gui

    global command_fifo
    global buffer_length_in_gui

    global log_string_array
    global log_text_label

    update_interval = 0.1
    # I don't use MUTEX-es here, because these variables are not being written into anywhere else.
    try:
        old_last_line_of_log = ''
        while keep_running:
            time.sleep(update_interval) # Slow down this loop a little
            robot_status_string_in_gui.set(robot_status)
            robot_pose_string_in_gui.set("Pose: " + str(robot_pose))
            robot_force_string_in_gui.set('Force: ' + str(robot_force) + 'N')
            buffer_length_in_gui.set(str(len(command_fifo)))
            """
            # Attempt 1. Delete contents of text widget, and fill the entire thing
            log_text_label.delete("1.0","end") # Delete the contents of the Text widget
            log_text_label.insert(tk.INSERT, log_string_array) # Fill the text widget with the latest string
            #log_text_label.insert(1.0, log_string_array) # Fill the text widget with the latest string
            # Scroll to the end
            #log_text_label.yview_moveto(1) # Scroll widget to the end
            log_text_label.see(tk.END)
            """
            """
            # Attempt 2. Find the last line of the string, and append it to the end.
            #print(log_string_array[0:len(log_string_array)-1].rindex('\n'))
                # Compensate for an off-by-one error, so the newline character is not in the exctracted line
            try:
                last_line_of_log = log_string_array[log_string_array[0:len(log_string_array)-1].rindex('\n')+1:len(log_string_array)]
                if (old_last_line_of_log != last_line_of_log):
                    # Only update the text window, if we have something to update with.
                    log_text_label.insert(tk.INSERT, last_line_of_log)
                    old_last_line_of_log = last_line_of_log

                log_text_label.see(tk.END) # keep it at the end
            except:
                # If we get garbage in the above statements, that means that the string search failed.
                # This is because the user deleted the log.
                # So, we delete the log text widget too.
                log_text_label.delete('1.0', 'end')
                log_string_array = str(unix_time_now_milliseconds())+ '; INFO; Log deleted by user.\n'
                log_text_label.insert(tk.INSERT, log_string_array)
            """
            # Attempt 3: Display the difference from strings.
            already_displayed_log = log_text_label.get('1.0', 'end-1c')
            if(len(already_displayed_log)+1 < len(log_string_array)):
                # If we got here, then we have stuff to add to the display.
                log_text_label.insert(tk.INSERT, log_string_array[len(already_displayed_log):len(log_string_array)])
                log_text_label.see(tk.END)

            if(len(already_displayed_log) > len(log_string_array)):
                # If we got here, then the log string was deleted, so we need to delete the window's contents.
                log_string_array = str(unix_time_now_milliseconds())+ '; INFO; Log deleted by user.\n'
                log_text_label.delete('1.0', 'end')
                log_text_label.insert(tk.INSERT, log_string_array)

    except:
        pass # Dirty trick for checking if these variables exist.

    if(debug_stuff):
        print('refresh_gui_function: Refresh GUI thread stopped.')
    return






program_start_timestamp = unix_time_now_milliseconds()

# We initialise our configparser object, and specify what comments to use.
configparser_object = ConfigParser(inline_comment_prefixes=';')

# These are global variables that we will use.
tcp_server_ip ='192.168.42.83'
tcp_port = 8472
connection_loss_timeout = 5 # in seconds
udp_server_ip = '127.0.0.1'
udp_port = 2501

log_string_array = ''


# For logging.
log_string_array = '\n' + str(unix_time_now_milliseconds()) + '; INFO; Software started.\n'



# Gui.
window_dimensions = '800x480'
window_position_offset = '+0+0'
window_font_size = 15
window_text_colour = 'lime'
window_background_colour = 'black'





if(os.path.exists('server_config.ini')):
    # we load the config file here.
    configparser_object.read('server_config.ini')

    # We read the config settings here.
    tcp_server_ip = configparser_object.get('Servers', 'tcp_server_ip', fallback=None)
    tcp_port = configparser_object.getint('Servers', 'tcp_port', fallback=None)
    connection_loss_timeout = configparser_object.getint('Servers', 'tcp_connection_loss_timeout', fallback=None)
    udp_server_ip = configparser_object.get('Servers', 'udp_server_ip', fallback=None)
    udp_port = configparser_object.getint('Servers', 'udp_port', fallback=None)
    window_dimensions = configparser_object.get('GUI', 'window_dimensions', fallback=None)
    window_position_offset = configparser_object.get('GUI', 'window_position_offset', fallback=None)
    window_font_size = configparser_object.getint('GUI', 'window_font_size', fallback=None)
    window_text_colour = configparser_object.get('GUI', 'window_text_colour', fallback=None)
    window_background_colour = configparser_object.get('GUI', 'window_background_colour', fallback=None)

    # Check if all values have been loaded
    if( (tcp_server_ip == None) or
        (tcp_port == None) or
        (connection_loss_timeout == None) or
        (udp_server_ip == None) or
        (udp_port == None) or
        (window_dimensions == None) or
        (window_position_offset == None) or
        (window_font_size == None) or
        (window_text_colour == None) or
        (window_background_colour == None)):
        config_file_parse_fail_alert()
else:
    # Throw a warning message
    messagebox.showwarning('Robot interface server', 'The server config file was not found. A new config file has been created, please check and edit as necessary.')
    # Create the example config file, with the hard-coded stuff above.
    # It seems that configparser has issues creating comments in the file. Here is my workaround.
    configparser_object['Servers'] = {
        '; this is the server the robot connects to: this should be your local address in text format, such as: tcp_server_ip': '192.168.42.100',
        'tcp_server_ip': tcp_server_ip,
        'tcp_port': tcp_port,
        'tcp_connection_loss_timeout': connection_loss_timeout,
        '; this one is your software interface: this runs on the same computer you are running your other scripts from, it can be set as: udp_server_ip': '127.0.0.1',
        'udp_server_ip': udp_server_ip,
        'udp_port': udp_port
    }
    configparser_object['GUI'] = {
        '; you can specify the window size, in pixels: window_size': '800x480',
        'window_dimensions': window_dimensions,
        '; For multi-monitor set-ups, you can manually set the window position, so the window will be placed to a different monitor. window_position_offset': '+x+y',
        'window_position_offset': window_position_offset,
        'window_font_size': '15',
        'window_text_colour': 'lime',
        'window_background_colour': '#111111'



    }
    # This one came from: https://docs.python.org/3/library/configparser.html#safeconfigparser-objects
    with open('server_config.ini', 'w') as configfile:
        configparser_object.write(configfile)

# At this point, we have our config file, and loaded the correct settings. Now we can continue with our lives.


# Server-specific global variables. For simplicity, we assemble these into a structure, and toss them to the threads.
# This way, I can lock them and

# These are the global variables our little robot uses. Other threads access them.
robot_busy = True # Start from the robot being busy.
robot_status = 'Robot not connected.' # This is just a string.
robot_pose = "(no data)" # Also a string
robot_force = "(no data)" # This is a single number as text.

# These are written into by the UDP server thread, and read from in the main thread.
command_fifo = deque() # Bunch of string.

# Just to start with, we add some dummy instructions here.
command_fifo.append(("nothing", ""))


keep_running = True # This is a simple control variable to kill my loops.





# Define our threads. Python 3 style, in classes.


class tcp_server_thread_class(threading.Thread):
    def __init__(self, name):
        threading.Thread.__init__(self)
        self.name = name
    def run(self):
        pass
        tcp_server_function()

class udp_server_thread_class(threading.Thread):
    def __init__(self, name):
        threading.Thread.__init__(self)
        self.name = name
    def run(self):
        udp_server_function()

class refresh_gui_thread_class(threading.Thread):
    def __init__(self, name):
        threading.Thread.__init__(self)
        self.name = name
    def run(self):
        pass
        refresh_gui_function()



# Now we can initialise our threads
tcp_server = tcp_server_thread_class('TCP Server')
udp_server = udp_server_thread_class('UDP Server')
gui_refresher = refresh_gui_thread_class('GUI Refresher')


# Start the threads.

tcp_server.start()
udp_server.start()
gui_refresher.start()

thread_lock = threading.Lock() # If we write into a variable from the main thread, we need to lock the variable.


# Start the GUI.

# We only have one window.
window = tk.Tk()
window.geometry(window_dimensions) # Window size
window.geometry(window_position_offset) # window position, from 0,0 pixel coordinates.
window.minsize(800, 480) # Set minimum size for the window. This overwrites the ini file.
window.resizable(True,True)
window.title("Volciclab Robot/Matlab server")
window.configure(bg=window_background_colour)
window.attributes('-toolwindow') # This should remove the buttons, but it doesn't seem to do anyting.






window.protocol("WM_DELETE_WINDOW", quit_button_function) # Handle the close button thing.

# These are the 'dynamic' variables that the GUI refresher thread updates
robot_status_string_in_gui = tk.StringVar(window, robot_status) # This is a class.
robot_pose_string_in_gui = tk.StringVar(window, robot_pose)
robot_force_string_in_gui = tk.StringVar(window, robot_force)
buffer_length_in_gui = tk.StringVar(window, str(len(command_fifo) -1))
#server_log_string_in_gui = tk.StringVar(window, log_string_array)


# Top frame. Robot status, buffer occupancy, and server details
status_frame = tk.Frame(master=window, height=100, bg=window_background_colour)

rubric_with = 390
rubric_height_status_frame = 60
rubric_height_info_frame = 90

# Top left. Robot status.
robot_status_frame = tk.Frame(master=status_frame, width = rubric_with, height = rubric_height_status_frame, bg=window_background_colour, highlightbackground=window_text_colour, highlightthickness=1)
"""
robot_status_text1 = tk.Label(master=robot_status_frame, text = "Robot's status, as reported by the robot:", font = ("Helvetica", round(window_font_size/1.5)), background=window_background_colour, foreground=window_text_colour)
robot_status_text1.pack(side=tk.TOP)
"""
robot_status_text = tk.Label(master=robot_status_frame, textvariable = robot_status_string_in_gui, font = ("Helvetica", round(window_font_size/1.25)), background=window_background_colour, foreground=window_text_colour)
robot_status_text.pack(side=tk.TOP)

robot_pose_text = tk.Label(master=robot_status_frame, textvariable = robot_pose_string_in_gui, font = ("Helvetica", round(window_font_size/2)), background=window_background_colour, foreground=window_text_colour)
robot_pose_text.pack(side=tk.TOP, anchor="w", padx = 10)

robot_force_text = tk.Label(master=robot_status_frame, textvariable = robot_force_string_in_gui, font = ("Helvetica", round(window_font_size/2)), background=window_background_colour, foreground=window_text_colour)
robot_force_text.pack(side=tk.BOTTOM, anchor = "w", padx = 10)


robot_status_frame.pack(side=tk.LEFT, fill='both', padx=5, pady=5, expand=True)
robot_status_frame.pack_propagate(0) # This propagate thing hard-codes the frame dimensions, initially.

# Top right: Buffer length.
buffer_length_frame = tk.Frame(master=status_frame, width=rubric_with, height=rubric_height_status_frame, bg=window_background_colour, highlightbackground=window_text_colour, highlightthickness=1)

buffer_length_text1 = tk.Label(master = buffer_length_frame, text = 'Instructions waiting in buffer:', font = ("Helvetica", window_font_size), background=window_background_colour, foreground=window_text_colour)
buffer_length_text1.pack(side=tk.TOP)

buffer_length_text2 = tk.Label(master = buffer_length_frame, textvariable=buffer_length_in_gui, font = ("Helvetica", window_font_size), background=window_background_colour, foreground=window_text_colour)
buffer_length_text2.pack(side=tk.BOTTOM)

buffer_length_frame.pack(side = tk.LEFT, fill='both', padx=5, pady=5, expand=True)
buffer_length_frame.pack_propagate(0)

status_frame.pack(fill='x', side=tk.TOP, expand=False)


info_frame = tk.Frame(master=window, height=100, bg=window_background_colour)

# Bottom left: UDP server status frame.
udp_server_status_frame = tk.Frame(master=info_frame, width=rubric_with, height = rubric_height_info_frame, bg=window_background_colour, highlightbackground=window_text_colour, highlightthickness=1)

udp_server_status_text1 = tk.Label(master = udp_server_status_frame, text = 'Your code connects to:\n' + udp_server_ip, font = ("Helvetica", window_font_size), background=window_background_colour, foreground=window_text_colour)
udp_server_status_text1.pack(side=tk.TOP)

udp_server_status_text2 = tk.Label(master = udp_server_status_frame, text = 'UDP Port: ' + str(udp_port), font = ("Helvetica", window_font_size), background=window_background_colour, foreground=window_text_colour)
udp_server_status_text2.pack(side=tk.BOTTOM)

udp_server_status_frame.pack(side=tk.LEFT, fill='both', padx=5, pady=5, expand=True)
udp_server_status_frame.pack_propagate(0)


# Bottom right: TCP server status frame
tcp_server_status_frame = tk.Frame(master=info_frame, width=rubric_with, height = rubric_height_info_frame, bg=window_background_colour, highlightbackground=window_text_colour, highlightthickness=1)

tcp_server_status_text1 = tk.Label(master = tcp_server_status_frame, text = ' Robot connects to:\n' + tcp_server_ip, font = ("Helvetica", window_font_size), background=window_background_colour, foreground=window_text_colour)
tcp_server_status_text1.pack(side=tk.TOP)

tcp_server_status_text2 = tk.Label(master = tcp_server_status_frame, text = 'TCP Port: ' + str(tcp_port), font = ("Helvetica", window_font_size), background=window_background_colour, foreground=window_text_colour)
tcp_server_status_text2.pack(side=tk.BOTTOM)

tcp_server_status_frame.pack(side=tk.LEFT, fill='both', padx=5, pady=5, expand=True)
tcp_server_status_frame.pack_propagate(0)



info_frame.pack(fill='x', side=tk.TOP, expand=False)



# Right click menu
right_click_menu = tk.Menu(window, tearoff=0)
right_click_menu.add_separator()
right_click_menu.add_command(label="Copy current pose to clipboard", command = copy_pose_to_clipboard)
right_click_menu.add_separator()
right_click_menu.add_separator()
right_click_menu.add_command(label="Save log to file", command=save_logfile)
right_click_menu.add_command(label="Clear log", command=clear_log)
right_click_menu.add_separator()
right_click_menu.add_separator()
right_click_menu.add_command(label="Terminate server", command=quit_button_function)
#right_click_menu.add_separator()

# This is how we assign the right click menu.
#.bind("<Button-3>", right_click_event_handler)

# Bottom frame. The log goes here.
bottom_frame = tk.Frame(master=window, height=280, bg=window_background_colour, highlightbackground=window_text_colour, highlightthickness=1)

# Add title
bottom_frame_title = tk.Label(master= bottom_frame, text="Server log. Right click for options.", font=("Helvetica", round(window_font_size/1.5)), background=window_background_colour, foreground=window_text_colour)
bottom_frame_title.configure(justify='right')
bottom_frame_title.pack(side=tk.TOP, anchor="w")



# Add the actual log output

#log_text_label = tk.Label(master = bottom_frame, textvariable =server_log_string_in_gui, font = ("Courier New", round(window_font_size/2)), background=window_background_colour, foreground=window_text_colour)
#log_text_label = tk.Entry(master = bottom_frame, textvariable =server_log_string_in_gui, font = ("Courier New", round(window_font_size/2)), background=window_background_colour, foreground=window_text_colour)
log_text_label = tk.Text(master=bottom_frame, font = ("Courier New", round(window_font_size/2)), background=window_background_colour, foreground=window_text_colour)
#log_text_label.configure(justify='left', anchor='w') # This is for the Label() widget
#log_text_label.configure(justify='left',state = 'readonly', disabledbackground=window_background_colour, bg=window_background_colour, readonlybackground=window_background_colour) # This is for the Entry() widget
log_text_label.bind("<Button-3>", right_click_event_handler)

#log_text_scrollbar = tk.Scrollbar(master=bottom_frame, bg=window_background_colour, highlightcolor=window_text_colour, highlightbackground=window_background_colour)

# Now we assign this scrollbar to he Text widget.
#log_text_label.configure(yscrollcommand=log_text_scrollbar.set)

#log_text_scrollbar.pack(side=tk.RIGHT, fill='y')
log_text_label.pack(side=tk.LEFT, anchor='s', fill='both', expand=True)

bottom_frame.pack(fill='both', side=tk.TOP, padx=5, pady=5, expand=True)

# Add the right click menu here
window.bind("<Button-3>", right_click_event_handler)



# This will hog on the main loop. Once the window is closed, this will release, and allow the program further.
window.mainloop()

# Stop the threads when the main loop stops
keep_running = False # Variable to end the threads' infinite loops
tcp_server.join()
print('tcp_server stopped.')
udp_server.join()
print('udp_server stopped.')
gui_refresher.join()
print('gui_refresher stopped')


quit()

